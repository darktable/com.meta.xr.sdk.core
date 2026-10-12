/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Settings;
using UnityEngine;

namespace Meta.XR.AI.AgentBridge
{
    /// <summary>
    /// Muse Code (Meta's first-party coding harness) integration. Drives the headless
    /// <c>muse exec --json</c> run and streams its JSONL events into ConversationManager,
    /// mirroring how ClaudeCodeService drives <c>claude -p --output-format stream-json</c>.
    /// Muse has no ACP transport, so this uses the one-shot exec path with <c>--session-id</c>
    /// for multi-turn continuity.
    /// </summary>
    [RegisterAIService(ServiceId, "Muse Code", Priority = 50, ExecutableName = "muse", SkillsSubPath = ".agents/skills")]
    public class MuseCodeService : AIServiceBase, IServiceCommandLineArguments, IServiceValidation, IServiceSettingsUI
    {
        /// <summary>The unique service identifier for Muse Code.</summary>
        public const string ServiceId = "musecode";

        private const int ExecutionTimeoutMinutes = 10;

        /// <summary>Settings specific to the Muse Code service.</summary>
        public static class MuseCodeSettings
        {
            private static readonly IIdentified Owner = new MuseCodeDescriptor();

            internal static readonly UserString ExecutablePath = new UserString
            {
                Uid = nameof(ExecutablePath),
                Owner = Owner,
                Default = "",
                Label = "Executable Path",
                Tooltip = "Optional path to the Muse Code executable. Leave empty to use PATH.",
                SendTelemetry = false
            };

            internal static readonly UserString AdditionalArguments = new UserString
            {
                Uid = nameof(AdditionalArguments),
                Owner = Owner,
                Default = "",
                Label = "Additional Arguments",
                Tooltip = "Arguments appended to the `muse exec` command.",
                SendTelemetry = false
            };

            private class MuseCodeDescriptor : IIdentified
            {
                public string Id => "AgentBridge.MuseCode";
            }
        }

        private readonly SemaphoreSlim _executionSemaphore = new(1, 1);
        private readonly SemaphoreSlim _validationSemaphore = new(1, 1);
        private readonly object _processLock = new();
        private Process? _currentProcess;
        private bool _cancellationRequested;
        private bool _disposed;
        private ValidationResult _currentValidationResult = ValidationResult.Unknown();

        public MuseCodeService() => CleanupOldTempFiles();

        // Per-turn streaming state. Held in a per-invocation object (not instance fields) so a stale
        // process's async stdout callback can't bleed into the next turn's bubble, error, or session.
        internal sealed class TurnContext
        {
            public string MessageId = string.Empty;
            public CallerIdentity? Caller;
            public string? Error;
            public bool SawTerminal;
            public bool SawDelta;
            public bool Closed;
            public string? LastSessionId;
        }

        /// <inheritdoc/>
        public override string ServiceName => "Muse Code";

        /// <inheritdoc/>
        public override bool HasActiveSession => !string.IsNullOrEmpty(ConversationManager.GetSessionId());

        /// <inheritdoc />
        public string AdditionalCommandLineArguments
        {
            get => MuseCodeSettings.AdditionalArguments.Value;
            set
            {
                if (value != MuseCodeSettings.AdditionalArguments.Value)
                {
                    MuseCodeSettings.AdditionalArguments.SetValue(value);
                    _currentValidationResult = ValidationResult.Unknown();
                }
            }
        }

        /// <summary>Process user input through the Muse Code CLI (<c>muse exec --json</c>).</summary>
        public override async Task ProcessUserInputAsync(
            string userInput,
            CallerIdentity? caller,
            List<ImageAttachment>? images = null,
            CancellationToken cancellationToken = default,
            string? systemPrompt = null)
        {
            await _executionSemaphore.WaitAsync(cancellationToken);
            try
            {
                Log.Info($"Processing user input through Muse Code: {userInput}");

                ConversationManager.IsActive = true;
                ConversationManager.ClearError();
                ConversationManager.AddMessage("user", userInput, caller: caller);

                await ExecuteMuseExecAsync(userInput, images, systemPrompt, caller);

                ConversationManager.ClearError();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                ConversationManager.SetError(ex.GetType() == typeof(Exception)
                    ? ex.Message
                    : $"{ex.Message} ({ex.GetType().Name})");
            }
            finally
            {
                ConversationManager.IsActive = false;
                _executionSemaphore.Release();
            }
        }

        private async Task ExecuteMuseExecAsync(
            string userInput, List<ImageAttachment>? images, string? systemPrompt, CallerIdentity? caller)
        {
            // Reset any stale cancel from a previous turn before any throwable setup, so a setup
            // failure this turn isn't misread as a cancel (which would silently drop the user's message).
            _cancellationRequested = false;

            var currentSessionId = ConversationManager.GetSessionIdForCaller(caller);
            var newSession = string.IsNullOrEmpty(currentSessionId);
            var projectRoot = GetProjectPath();

            var ctx = new TurnContext { MessageId = $"muse_{Guid.NewGuid():N}", Caller = caller };
            var tempFiles = new List<string>();

            // Everything that creates temp files runs inside the try so the finally always deletes
            // them, even if argument parsing or process setup throws.
            try
            {
                // muse exec has no --system-prompt; fold it into the first prompt of a new session.
                var promptText = newSession && !string.IsNullOrEmpty(systemPrompt)
                    ? systemPrompt + "\n\n" + userInput
                    : userInput;

                // Pass the prompt via a file, not stdin/argv: muse is a managed launcher whose Windows
                // sandbox can swallow stdin, and a file also sidesteps argv length/quoting limits.
                var promptFile = Path.Combine(
                    Application.temporaryCachePath, $"muse_prompt_{Guid.NewGuid():N}.txt");
                File.WriteAllText(promptFile, promptText, new UTF8Encoding(false));
                tempFiles.Add(promptFile);

                var imagePaths = WriteImageFiles(images, tempFiles);

                var processStartInfo = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = projectRoot,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                // Windows ships `muse.cmd`, which CreateProcess can't launch directly; ConfigureExecutable
                // routes it through cmd.exe (as OpenCodeService does).
                ConfigureExecutable(
                    processStartInfo, ResolveExecutable(MuseCodeSettings.ExecutablePath.Value, "muse"));
                ApplyLoginShellPath(processStartInfo);
                foreach (var arg in BuildExecArgs(
                    projectRoot, newSession ? null : currentSessionId, promptFile, imagePaths))
                {
                    processStartInfo.ArgumentList.Add(arg);
                }
                CommandLineArguments.AppendTo(processStartInfo, AdditionalCommandLineArguments);

                using var process = new Process { StartInfo = processStartInfo };
                lock (_processLock)
                {
                    _currentProcess = process;
                }

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        ProcessOutputLine(e.Data, ctx);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log.Warning($"Muse Code stderr: {e.Data}");
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var timeout = TimeSpan.FromMinutes(ExecutionTimeoutMinutes);
                var completed = await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds));
                if (!completed)
                {
                    CommandLineArguments.KillProcessTree(process);
                    // KillProcessTree already did a bounded wait; this off-thread bounded wait is a
                    // best-effort settle for the killed process and (unlike a bare synchronous
                    // WaitForExit()) cannot freeze the Editor. It does NOT guarantee the async readers
                    // have flushed -- the ctx.Closed guard in MapLine is what stops a late delta from
                    // re-opening the bubble. Swallowed so a drain failure can't mask the TimeoutException.
                    try { await Task.Run(() => process.WaitForExit(5000)); } catch { }
                    throw new TimeoutException(
                        $"Muse Code did not respond within {timeout.TotalMinutes} minutes. " +
                        "Check your network connection and Muse Code authentication.");
                }

                // The timed WaitForExit can return before OutputDataReceived has processed the final
                // run.terminal.* line; the parameterless wait drains those async callbacks (and gives
                // the memory barrier for reading ctx, which the callbacks write on a pool thread).
                await Task.Run(() => process.WaitForExit());

                var exitCode = process.ExitCode;
                Log.Info($"Muse Code exited with code: {exitCode}");
                if (_cancellationRequested)
                    return;

                if (ctx.Error != null)
                    throw new Exception(ctx.Error);
                // Only treat a nonzero exit as failure when no terminal event already reported success.
                if (exitCode != 0 && !ctx.SawTerminal)
                    throw new Exception($"Muse Code exited with code {exitCode}");
            }
            catch (Exception ex)
            {
                if (_cancellationRequested)
                {
                    Log.Info("Muse Code run cancelled");
                    return;
                }
                Log.Error($"Exception running Muse Code: {ex.Message}");
                throw;
            }
            finally
            {
                // Single finalization point: close a streamed-but-never-terminated bubble on any exit
                // path (timeout/cancel/nonzero-exit). No-op on the success path because a completed/failed
                // emit already set ctx.Closed.
                FinalizeTurn(ctx, AddAssistantMessage);
                foreach (var f in tempFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); }
                    catch (Exception ex) { Log.Warning($"Failed to delete temp file {f}: {ex.Message}"); }
                }
                lock (_processLock)
                {
                    _currentProcess = null;
                }
            }
        }

        /// <summary>Translate one muse exec JSONL line and forward it to ConversationManager.</summary>
        private void ProcessOutputLine(string line, TurnContext ctx)
        {
            try
            {
                var ev = MuseExecJsonParser.Parse(line);
                MapLine(ev, ctx, AddAssistantMessage,
                    (caller, sid) => MainThreadDispatcher.ExecuteOnMainThread(
                        () => ConversationManager.SetSessionIdForCaller(caller, sid)));
            }
            catch (Exception ex)
            {
                Log.Warning($"Error processing Muse Code output line: {ex.Message}");
            }
        }

        /// <summary>
        /// Pure mapping of one parsed muse event onto the emit/session-id side effects. No
        /// MainThreadDispatcher or ConversationManager references, so the turn state machine is
        /// unit-testable. <paramref name="emit"/> is (messageId, content, caller, isDelta, streaming).
        /// </summary>
        internal static void MapLine(
            MuseExecEvent ev,
            TurnContext ctx,
            Action<string, string, CallerIdentity?, bool, bool> emit,
            Action<CallerIdentity?, string> setSessionId)
        {
            // The session id is constant for a turn; only dispatch when it first appears/changes
            // (SetSessionIdForCaller persists to disk and raises an event). An empty id is ignored so
            // it can't overwrite a real one, matching the resume-side string.IsNullOrEmpty check.
            if (!string.IsNullOrEmpty(ev.SessionId) && ev.SessionId != ctx.LastSessionId)
            {
                ctx.LastSessionId = ev.SessionId;
                setSessionId(ctx.Caller, ev.SessionId!);
            }

            switch (ev.Kind)
            {
                case MuseExecEventKind.AssistantDelta:
                    // Idempotent after close: once the turn is closed (a terminal event or FinalizeTurn),
                    // a late delta flushed by a descendant that outlived KillProcessTree must not re-open
                    // the streamed bubble -- doing so would resurrect the forever-spinning state on disk.
                    if (ctx.Closed)
                        break;
                    ctx.SawDelta = true;
                    emit(ctx.MessageId, ev.Text ?? string.Empty, ctx.Caller, true, true);
                    break;

                case MuseExecEventKind.TurnCompleted:
                    // Last terminal wins: a completed run clears any earlier recorded error.
                    ctx.SawTerminal = true;
                    ctx.Error = null;
                    if (!string.IsNullOrEmpty(ev.Text))
                        // Replace (not append) with the authoritative full text and close streaming.
                        emit(ctx.MessageId, ev.Text!, ctx.Caller, false, false);
                    else if (ctx.SawDelta)
                        // Empty terminal after deltas: close only; isDelta:true so the streamed
                        // partial text is NOT erased.
                        emit(ctx.MessageId, string.Empty, ctx.Caller, true, false);
                    ctx.Closed = true;
                    break;

                case MuseExecEventKind.TurnFailed:
                    // Recorded here, thrown after the process exits so ProcessUserInputAsync reports a
                    // failed send. If text was streamed, close that bubble so it doesn't spin behind the
                    // error; with no delta there's no bubble and the error is surfaced later.
                    ctx.SawTerminal = true;
                    ctx.Error = ev.Error;
                    if (ctx.SawDelta)
                    {
                        emit(ctx.MessageId, string.Empty, ctx.Caller, true, false);
                        ctx.Closed = true;
                    }
                    break;
            }
        }

        /// <summary>
        /// Closes a streamed-but-never-terminated bubble on abnormal exit (timeout/cancel/nonzero-exit)
        /// and always marks the turn closed. It emits only when text was streamed and nothing else has
        /// closed the bubble yet.
        /// </summary>
        internal static void FinalizeTurn(
            TurnContext ctx, Action<string, string, CallerIdentity?, bool, bool> emit)
        {
            if (ctx.SawDelta && !ctx.Closed)
                emit(ctx.MessageId, string.Empty, ctx.Caller, true, false);

            // Mark the turn closed on every path -- FinalizeTurn runs in the finally at turn end, so the
            // turn is over whether or not anything streamed. This arms MapLine's delta guard even when
            // no delta was seen; otherwise a late delta on the no-delta timeout path (the one exit that
            // skips the parameterless drain) would open a new, permanently spinning, persisted bubble.
            ctx.Closed = true;
        }

        private void AddAssistantMessage(
            string messageId, string content, CallerIdentity? caller, bool isDelta, bool streaming)
        {
            MainThreadDispatcher.ExecuteOnMainThread(() =>
            {
                ConversationManager.AddMessage(new ConversationMessage
                {
                    MessageId = messageId,
                    MessageType = "assistant",
                    Content = content,
                    CallerId = caller?.Id ?? string.Empty,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    IsStreaming = streaming,
                    IsDelta = isDelta
                });
            });
        }

        /// <summary>
        /// Builds the fixed `muse exec` argument list (everything except the user's Additional
        /// Arguments). Pure, so the argv contract is unit-testable without spawning the CLI.
        /// </summary>
        internal static List<string> BuildExecArgs(
            string projectRoot, string? sessionId, string promptFile, IReadOnlyList<string>? imagePaths)
        {
            // SECURITY: the default skips interactive tool approval (--approval-mode never) so the
            // headless run can use tools/skills unattended, but KEEPS Muse's OS sandbox on. Full
            // autonomy (--yolo, which also disables the sandbox) stays opt-in via the Additional
            // Arguments setting, appended by CommandLineArguments.AppendTo at the call site.
            var args = new List<string>
            {
                "exec", "--json", "--approval-mode", "never", "--workspace", projectRoot,
                "--trust-workspace"
            };
            if (!string.IsNullOrEmpty(sessionId))
            {
                args.Add("--session-id");
                args.Add(sessionId!);
            }
            args.Add("--prompt-file");
            args.Add(promptFile);
            if (imagePaths != null)
            {
                foreach (var path in imagePaths)
                {
                    args.Add("--image");
                    args.Add(path);
                }
            }
            return args;
        }

        /// <summary>Writes image attachments to temp files, returning their paths (also tracked for cleanup).</summary>
        private static List<string> WriteImageFiles(List<ImageAttachment>? images, List<string> tempFiles)
        {
            var paths = new List<string>();
            if (images == null || images.Count == 0)
                return paths;

            foreach (var image in images)
            {
                if (string.IsNullOrEmpty(image.Data))
                    continue;

                var extension = ".png";
                if (!string.IsNullOrEmpty(image.MediaType))
                {
                    if (image.MediaType.Contains("jpeg") || image.MediaType.Contains("jpg")) extension = ".jpg";
                    else if (image.MediaType.Contains("gif")) extension = ".gif";
                    else if (image.MediaType.Contains("webp")) extension = ".webp";
                }

                try
                {
                    var imagePath = Path.Combine(
                        Application.temporaryCachePath, $"muse_image_{Guid.NewGuid():N}{extension}");
                    File.WriteAllBytes(imagePath, Convert.FromBase64String(image.Data));
                    tempFiles.Add(imagePath);
                    paths.Add(imagePath);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to write Muse Code image attachment: {ex.Message}");
                }
            }
            return paths;
        }

        private static string GetProjectPath()
        {
            // Application.dataPath is "<project>/Assets"; the workspace root is one level up.
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        // Reclaim prompt/image temp files orphaned by a crash or editor kill mid-run (the per-turn
        // finally covers the normal case). Prompt files contain prompt text, so don't leave them.
        private static void CleanupOldTempFiles()
        {
            try
            {
                var tempDir = Application.temporaryCachePath;
                if (!Directory.Exists(tempDir))
                    return;

                var patterns = new[]
                {
                    "muse_prompt_*.txt", "muse_image_*.png", "muse_image_*.jpg",
                    "muse_image_*.gif", "muse_image_*.webp"
                };
                foreach (var pattern in patterns)
                {
                    foreach (var file in Directory.GetFiles(tempDir, pattern))
                    {
                        try { File.Delete(file); }
                        catch (Exception ex) { Log.Warning($"Failed to delete old temp file {file}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error during Muse Code temp cleanup: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public override void ClearSession()
        {
            Log.Info("Clearing Muse Code session");
            ConversationManager.Clear();
        }

        /// <inheritdoc/>
        public override async Task CancelCurrentOperationAsync()
        {
            Process? processToKill = null;
            lock (_processLock)
            {
                if (IsProcessRunning(_currentProcess))
                {
                    processToKill = _currentProcess;
                    _cancellationRequested = true;
                }
            }

            if (processToKill != null)
            {
                try
                {
                    Log.Info("Cancelling Muse Code operation");
                    CommandLineArguments.KillProcessTree(processToKill);
                    await Task.Run(() => processToKill.WaitForExit(5000));
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error killing Muse Code process: {ex.Message}");
                }
            }
        }

        /// <inheritdoc />
        public override string? GetResumeCommand(string sessionId)
        {
            var executable = ResolveExecutable(MuseCodeSettings.ExecutablePath.Value, "muse");
            return $"{executable} resume {sessionId}";
        }

        #region IServiceSettingsUI

        /// <summary>Render Muse Code settings (simple version for 3P compatibility).</summary>
        public void DrawSettingsUI()
        {
            UnityEditor.EditorGUILayout.LabelField("Muse Code Settings", UnityEditor.EditorStyles.boldLabel);
            UnityEditor.EditorGUILayout.LabelField("Executable Path", MuseCodeSettings.ExecutablePath.Value);
        }

        /// <summary>Render Muse Code settings with the Meta settings infrastructure.</summary>
        public void DrawSettingsUI(Origins origins, IIdentified originData)
        {
            UnityEditor.EditorGUILayout.LabelField("Muse Code Settings", UnityEditor.EditorStyles.boldLabel);
            MuseCodeSettings.ExecutablePath.DrawForGUI(origins, originData, OnExecutablePathChanged);
        }

        /// <summary>Reset Muse Code settings to defaults.</summary>
        public void ResetSettingsToDefaults()
        {
            MuseCodeSettings.ExecutablePath.Reset();
            MuseCodeSettings.AdditionalArguments.Reset();
            OnExecutablePathChanged();
        }

        private void OnExecutablePathChanged()
        {
            ClearResolvedExecutablePath();
            _currentValidationResult = ValidationResult.Unknown();
        }

        #endregion

        #region IServiceValidation

        /// <inheritdoc />
        public ValidationResult CurrentValidationResult => _currentValidationResult;

        /// <inheritdoc />
        public async Task<ValidationResult> ValidateConfigurationAsync()
        {
            if (!await _validationSemaphore.WaitAsync(CommandLineArguments.ValidationQueueTimeoutMilliseconds))
                return CommandLineArguments.ValidationAlreadyInProgress();
            try
            {
                _currentValidationResult = ValidationResult.Validating();

                var configuredPath = MuseCodeSettings.ExecutablePath.Value;
                if (!string.IsNullOrEmpty(configuredPath) && !File.Exists(configuredPath))
                {
                    _currentValidationResult = ValidationResult.Invalid($"Executable not found: {configuredPath}");
                    return _currentValidationResult;
                }

                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                // muse ships as muse.cmd on Windows; ConfigureExecutable routes it through cmd.exe.
                ConfigureExecutable(psi, ResolveExecutable(configuredPath, "muse"));
                ApplyLoginShellPath(psi);
                psi.ArgumentList.Add("--version");

                using var process = new Process { StartInfo = psi };
                var output = new StringBuilder();
                var error = new StringBuilder();

                process.Start();
                // Drain BOTH streams on background threads: a chatty stderr that fills its pipe would
                // otherwise deadlock the child and trip the timeout (mirrors ClaudeCode/OpenCode).
                var readOut = Task.Run(() => { try { output.Append(process.StandardOutput.ReadToEnd()); } catch { } });
                var readErr = Task.Run(() => { try { error.Append(process.StandardError.ReadToEnd()); } catch { } });
                var completed = await Task.Run(() => process.WaitForExit(5000));
                if (!completed)
                {
                    CommandLineArguments.KillProcessTree(process);
                    _currentValidationResult = ValidationResult.Error("Muse Code CLI timed out");
                    return _currentValidationResult;
                }

                await Task.WhenAny(Task.WhenAll(readOut, readErr), Task.Delay(500));

                _currentValidationResult = process.ExitCode == 0
                    ? ValidationResult.Valid($"Muse Code CLI available ({output.ToString().Trim()})")
                    : ValidationResult.Invalid("Muse Code not found. Install Muse Code or set the executable path.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _currentValidationResult = ValidationResult.Invalid(
                    "Muse Code not found. Install Muse Code or set the executable path.");
            }
            catch (Exception ex)
            {
                _currentValidationResult = ValidationResult.Error($"Validation error: {ex.Message}");
            }
            finally
            {
                _validationSemaphore.Release();
            }

            return _currentValidationResult;
        }

        #endregion

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _executionSemaphore?.Dispose();
                _validationSemaphore?.Dispose();
                lock (_processLock)
                {
                    try
                    {
                        if (IsProcessRunning(_currentProcess))
                            CommandLineArguments.KillProcessTree(_currentProcess!);
                        _currentProcess?.Dispose();
                    }
                    catch { /* best-effort */ }
                    _currentProcess = null;
                }
            }
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
