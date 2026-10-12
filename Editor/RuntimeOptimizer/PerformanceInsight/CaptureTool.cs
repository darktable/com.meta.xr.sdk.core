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

using System;
using Unity.Profiling;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using RO = Meta.XR.RuntimeOptimizer.Core;
using Meta.XR.RuntimeOptimizer.Editor;

namespace Meta.XR.RuntimeOptimizer.Editor.PerformanceInsight
{
    static class CaptureTool
    {
        private const int kAdbCommandTimeoutMs = 30000;
        private const int kGpuProfilerCommandTimeoutMs = 10000;

        /// <summary>Seconds of render stage trace run after a launch so What If can profile the GPU.</summary>
        /// <remarks>
        /// The shortest duration the tool's <c>-t=N</c> form accepts. Nothing consumes this trace --
        /// it only has to run and finish -- so there is nothing to gain from a longer one, and every
        /// extra second is added to the wait behind the Launch button.
        /// </remarks>
        private const int kRenderStagePrimeSeconds = 1;

        /// <summary>How long to wait for init to report the GPU profiling service running.</summary>
        /// <remarks>
        /// init only has to fork an already-installed service, so this is generous. It bounds the
        /// cold path; the warm path returns on the first poll without ever sleeping.
        /// </remarks>
        private const int kGpuServiceReadyTimeoutMs = 5000;
        private const int kGpuServiceReadyPollMs = 250;
        static private int unprofiledAppPid = -1;

        /// <summary>Bound for the <c>adb pull</c> transfers.</summary>
        /// <remarks>
        /// Unlike the other commands these scale with file size and link speed -- a trace can be tens
        /// of megabytes -- so they get a bound generous enough that a slow but working transfer is
        /// never killed, while still being finite.
        /// </remarks>
        private const int kFileTransferTimeoutMs = 300000;

        static private OVRADBTool adbTool = CreateAdbTool();
        static private Thread captureThread = null;
        static private List<string> connectedDevices = new List<string>();
        private const string outFile = "/data/misc/perfetto-traces/trace";
        private const string gpuStreamFlushOutFile = "/data/misc/perfetto-traces/ro_gpu_stream_flush";
        static private System.Object lockObj = new System.Object();
        static private int metricProcessWorkInQueue = 0;
        static public int lastKnownPID = -1;

        /// <summary>Set when the app may have started a render stage trace of its own.</summary>
        /// <remarks>See <see cref="FlushGpuStreamAfterAppTrace"/>.</remarks>
        static private volatile bool appGpuTraceMayBeReleased = false;

        /// <summary>The app process that has already returned a quick perf GPU time.</summary>
        /// <remarks>
        /// From then on the app answers a quick perf requested while frozen from that cached value
        /// instead of tracing. Before it, the first freeze of every launch traces.
        /// </remarks>
        static private int appGpuTimeCachedPid = -1;

        static private string LowOverheadCaptureConfig = @"
buffers {
  size_kb: 4096
  fill_policy: DISCARD
}
}
buffers {
  size_kb: 43008
  fill_policy: DISCARD
}
buffers {
  size_kb: 262144
  fill_policy: DISCARD
}
data_sources {
  config {
    name: ""linux.process_stats""
    target_buffer: 0
    process_stats_config {
      scan_all_processes_on_start: false
    }
  }
}
data_sources {
  config {
    name: ""linux.ftrace""
    target_buffer: 1
    ftrace_config {
      ftrace_events: ""sched/sched_switch""
      ftrace_events: ""power/suspend_resume""
      ftrace_events: ""sched/sched_wakeup""
      ftrace_events: ""sched/sched_wakeup_new""
      ftrace_events: ""sched/sched_waking""
      ftrace_events: ""sched/sched_process_exit""
      ftrace_events: ""sched/sched_process_free""
      ftrace_events: ""task/task_newtask""
      ftrace_events: ""task/task_rename""
      ftrace_events: ""ftrace/print""
      buffer_size_kb: 8192
      drain_period_ms: 10
    }
  }
  producer_name_filter: ""{bundle_name}""
}
data_sources {
  config {
    name: ""track_event""
    target_buffer: 2
  }
}
trigger_config {
    trigger_mode: STOP_TRACING
    triggers {
        name: ""perf_optimizer_auto_stop""
        stop_delay_ms: 0
    }
    trigger_timeout_ms: 100000
  }
";

        // Sized against what the analysis consumes rather than what the GPU can produce.
        // MetricAPI bins frames with windows(2).take(200), so the capture only needs to clear
        // ~200 colour passes: 3000ms yields 199 and 64MB holds them, where the previous 256MB
        // collected ~500 and spent the surplus purely on capture and transfer time.
        //
        // Do not shorten the duration to save time. Measured on a Quest 3S, two runs each:
        //   1000ms -> 61 passes, GPU 15.03ms, 31.5s     2000ms -> 127 passes, GPU 14.84ms, 36.5s
        //   3000ms -> 199 passes, GPU 14.17ms, 36.4s
        // GPU reads systematically high on short windows -- runs agree with each other to 0.02%
        // while differing 5.7% across durations, so it is bias rather than noise -- and CPU
        // run-to-run spread is 7x worse at 1000ms. 3000ms costs the same wall time as 2000ms
        // because fixed perfetto setup dominates, so there is nothing to buy by going shorter.
        //
        // Keep gpu_renderstage out. The only metric render stages feed is "binning" (Binning pass
        // time per frame), which no insight reads; GPU frame time, MSAA and bin count come from
        // surface workloads and the rest from the GPU counters. Without them the trace loses its
        // per-stage slices (Bin, Render, LoadColor, StoreColor, StoreDepthStencil), which only
        // matter when reading it by hand.
        static private string captureConfig = @"
buffers {
  size_kb: 4096
  fill_policy: DISCARD
}
buffers {
  size_kb: 43008
  fill_policy: DISCARD
}
buffers {
  size_kb: 65536
  fill_policy: DISCARD
}
data_sources {
  config {
    name: ""linux.process_stats""
    target_buffer: 0
    process_stats_config {
      scan_all_processes_on_start: false
    }
  }
}
data_sources {
  config {
    name: ""linux.ftrace""
    target_buffer: 1
    ftrace_config {
      ftrace_events: ""sched/sched_switch""
      ftrace_events: ""power/suspend_resume""
      ftrace_events: ""task/task_newtask""
      ftrace_events: ""task/task_rename""
      ftrace_events: ""ftrace/print""
      atrace_apps: ""{bundle_name}""
      buffer_size_kb: 8192
      drain_period_ms: 10
      compact_sched {
       enabled: true
      }
    }
  }
}
data_sources {
  config {
    name: ""track_event""
    target_buffer: 2
    track_event_config {
      disabled_categories: ""*""
      disabled_categories: ""xr_runtime_server""
      disabled_categories: ""xr_runtime_client""
      disabled_categories: ""gpu_profiling_service""
      disabled_categories: ""gpu_profiling_service_low_frequency""
      disabled_categories: ""gpu_renderstage""
      disabled_categories: ""gpu_surface_workload""
      enabled_categories: ""gpu_profiling_service""
      enabled_categories: ""gpu_surface_workload""
    }
  }
}
duration_ms: 3000
";

        // Its trace is thrown away; see FlushGpuStreamAfterAppTrace for why it runs at all. Surface
        // workloads only, like captureConfig, so it stays off the render stage crash path.
        static private string GpuStreamFlushConfig = @"
buffers {
  size_kb: 4096
  fill_policy: DISCARD
}
data_sources {
  config {
    name: ""track_event""
    target_buffer: 0
    track_event_config {
      disabled_categories: ""*""
      enabled_categories: ""gpu_profiling_service""
      enabled_categories: ""gpu_surface_workload""
    }
  }
}
duration_ms: 1000
";

        static public string RemoveLastFolderFromPath(string path)
        {
            var lastSlashIndex = path.LastIndexOf('/');
            if (lastSlashIndex != -1)
            {
                return path.Substring(0, lastSlashIndex);
            }
            return path;
        }

        static private string CreateFolderIn(string path, string folderName)
        {
#if UNITY_EDITOR_OSX
        var wholePath = path + "/" + folderName;
#else
            var wholePath = path + "\\" + folderName;
#endif
            if (!Directory.Exists(wholePath))
            {
                Directory.CreateDirectory(wholePath);
            }
            return wholePath;
        }

        static public uint EpochTime()
        {
            DateTime epochStart = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            uint currentEpochTime = (uint)(DateTime.UtcNow - epochStart).TotalSeconds;

            return currentEpochTime;
        }

        static public string GetAssetOutputFolderName()
        {
            return "Assets/#Meta";
        }

        static public string GetOutputDirectory()
        {
            string projectPath = RemoveLastFolderFromPath(Application.dataPath);
            string outdir = Path.Combine(projectPath, GetAssetOutputFolderName());
            if (!Directory.Exists(outdir))
            {
                Directory.CreateDirectory(outdir);
            }
            return outdir;
        }

        static public string GetMetricJsonOutputPath(string traceFilePath)
        {
            string originalFilePath = traceFilePath;
            // New file extension
            string newExtension = ".json";
            // Get the file name without the extension
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(originalFilePath);
            // Create the new file path with the new extension
            return Path.Combine(Path.GetDirectoryName(originalFilePath), $"{fileNameWithoutExtension}{newExtension}");
        }

        static private void StartServerCallback()
        {

        }

        static private void PerfettoCapture()
        {

        }

        static public bool isDetailedGPUServiceEnabled()
        {
            string outputString;
            string errorString;
            const string commandCapture = "shell getprop debug.egl.profiler";
            adbTool.RunCommand(new string[] { "-s", connectedDevices[0], commandCapture }, PerfettoCapture, out outputString, out errorString);
            RO.Util.DebugLog("adb egl.profiler: " + outputString + " error: " + errorString);

            if (outputString.Contains("1"))
            {
                return true;
            }
            return false;
        }

        /// <summary>True while the background capture thread is alive and issuing adb commands.</summary>
        /// <remarks>
        /// <c>OVRADBTool</c> keeps its stdout and stderr StringBuilders in instance fields that every
        /// <c>RunCommand</c> reassigns and then nulls out, so two threads sharing one tool overwrite
        /// each other's output. Main-thread pollers must consult this before touching adb, or they
        /// corrupt whatever the capture thread is reading.
        /// </remarks>
        static public bool IsCaptureThreadRunning()
        {
            return captureThread != null && captureThread.IsAlive;
        }

        static public void ValidateProcess(string bundleName)
        {
            // Return early if IssuePerfettoCapture is currently running
            if (IsCaptureThreadRunning())
            {
                return;
            }
            lastKnownPID = GetProcessPID(bundleName);
        }


        static public bool ValidateAdb()
        {
            // Return early if IssuePerfettoCapture is currently running
            if (IsCaptureThreadRunning())
            {
                return true;
            }

            if (!IsADBReady())
            {
                return false;
            }

            connectedDevices = adbTool.GetDevices();

            if (connectedDevices.Count > 0)
            {
                return true;
            }
            else
            {
                connectedDevices.Clear();
            }

            RO.Util.DebugLog("No device connected");
            return false;
        }

        static public bool ForwardPort(int port)
        {
            int exitCode = adbTool.ForwardPort(port, null);
            return exitCode == 0;
        }

        static public bool ReleasePort(int port)
        {
            int exitCode = adbTool.ReleasePort(port, null);
            return exitCode == 0;
        }

        static public int GetProcessPID(string bundleName)
        {
            string outputString = GetProcessInfo(bundleName);

            RO.Util.DebugLog($"[GetProcessPID] Searching for PID of '{bundleName}'");

            if (string.IsNullOrEmpty(outputString))
            {
                RO.Util.DebugLog($"[GetProcessPID] No output from GetProcessInfo - app not running");
                return -1;
            }

            RO.Util.DebugLog($"[GetProcessPID] Raw output: '{outputString}'");

            // Parse the output to extract PID
            string[] parts = outputString.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            RO.Util.DebugLog($"[GetProcessPID] Split into {parts.Length} parts");
            for (int i = 0; i < parts.Length && i < 5; i++)
            {
                RO.Util.DebugLog($"[GetProcessPID] Part[{i}]: '{parts[i]}'");
            }

            if (parts.Length >= 2)
            {
                int pid;
                if (int.TryParse(parts[1], out pid))
                {
                    RO.Util.DebugLog($"[GetProcessPID] Successfully extracted PID: {pid}");
                    return pid;
                }
                else
                {
                    RO.Util.DebugLog($"[GetProcessPID] Failed to parse PID from parts[1]: '{parts[1]}'");
                }
            }
            else
            {
                RO.Util.DebugLog($"[GetProcessPID] Insufficient parts in output (need at least 2, got {parts.Length})");
            }

            // Try to parse just the first part as PID (in case pidof returns just the PID)
            if (parts.Length >= 1)
            {
                int pid;
                if (int.TryParse(parts[0], out pid))
                {
                    RO.Util.DebugLog($"[GetProcessPID] Successfully extracted PID from first part: {pid}");
                    return pid;
                }
            }

            RO.Util.DebugLog($"[GetProcessPID] Failed to extract PID from output");
            return -1;
        }

        static public string GetProcessInfo(string bundleName)
        {
            string errorString = string.Empty;

            if (!IsADBReady() || connectedDevices.Count == 0)
            {
                RO.Util.DebugLog($"[GetProcessInfo] ADB not ready or no devices connected");
                return null;
            }

            string outputString;
            // Use pidof command which is more reliable for finding process by package name
            // Falls back to ps | grep if pidof is not available
            string command = $"shell \"pidof {bundleName} 2>/dev/null || ps -A | grep {bundleName}\"";

            RO.Util.DebugLog($"[GetProcessInfo] Executing command: {command}");

            adbTool.RunCommand(new string[] { "-s", connectedDevices[0], command }, PerfettoCapture, out outputString, out errorString);

            // Debug logging to help diagnose detection issues
            RO.Util.DebugLog($"[GetProcessInfo] Bundle: '{bundleName}', Output: '{outputString}', Error: '{errorString}'");

            return !string.IsNullOrEmpty(outputString) && string.IsNullOrEmpty(errorString) ? outputString : null;
        }

        static public int ConnectedDeviceCount()
        {
            return connectedDevices.Count;
        }

        static public string[] ConnectedDevices()
        {
            return connectedDevices.ToArray();
        }

        /// <summary>Resolves the Android SDK root without depending on the Android module's editor assembly.</summary>
        /// <remarks>
        /// <c>OVRConfig</c> resolves the path from EditorPrefs, the embedded playback engine and
        /// <c>ANDROID_SDK_ROOT</c>, using only <c>UnityEditor</c> APIs. That keeps this file compiling
        /// whether or not the Android module is installed, and working regardless of the active build
        /// target. The try/catch matters because this runs from a static field initializer, where an
        /// escaping exception would leave the whole type uninitialized.
        /// </remarks>
        static private string GetAndroidSdkRootPath()
        {
            try
            {
                return OVRConfig.GetAndroidSDKPathLocation(false) ?? string.Empty;
            }
            catch (Exception e)
            {
                RO.Util.DebugLog("Failed to resolve the Android SDK root: " + e.Message);
                return string.Empty;
            }
        }

        /// <summary>Reports whether an ADB executable could be located, without starting the server or logging.</summary>
        /// <remarks>
        /// Every device operation in the Runtime Optimizer goes through ADB, so when this returns false
        /// the window cannot do anything useful.
        /// </remarks>
        static public bool IsAdbAvailable()
        {
            return OVRADBTool.IsAndroidSdkRootValid(GetAndroidSdkRootPath());
        }

        static private OVRADBTool CreateAdbTool()
        {
            return new OVRADBTool(GetAndroidSdkRootPath())
            {
                defaultTimeoutMs = kAdbCommandTimeoutMs
            };
        }

        static public bool IsADBReady()
        {
            if (adbTool == null)
            {
                adbTool = CreateAdbTool();
            }
            int code = adbTool.StartServer(StartServerCallback);
            if (code != 0)
            {
                RO.Util.DebugLog("adb StartServer failed: " + code.ToString());
                RO.Util.DebugLog("Please check your Android sdk path in Preferences/External Tools");
                adbTool = null;
                return false;
            }
            return true;
        }

        static public bool KillApp(string packageName)
        {
            string outputString;
            string errorString;
            string command = "shell am force-stop " + packageName;

            int result = adbTool.RunCommand(new string[] { "-s", connectedDevices[0], command }, PerfettoCapture, out outputString, out errorString);

            if (result != 0 || !string.IsNullOrEmpty(errorString))
            {
                UnityEngine.Debug.LogError($"Failed to kill app: {packageName}. Error: {errorString}");
                return false;
            }

            RO.Util.DebugLog($"Successfully killed app: {packageName}");
            return true;
        }

        static public bool LaunchApp(string packageActivityPath)
        {
            if (!IsADBReady() || connectedDevices.Count == 0)
            {
                UnityEngine.Debug.LogError("ADB not ready or no devices connected");
                return false;
            }

            string outputString;
            string errorString;
            string command = "shell am start -n " + packageActivityPath;

            int result = adbTool.RunCommand(new string[] { "-s", connectedDevices[0], command }, PerfettoCapture, out outputString, out errorString);

            if (result != 0 || !string.IsNullOrEmpty(errorString))
            {
                UnityEngine.Debug.LogError($"Failed to launch app: {packageActivityPath}. Error: {errorString}");
                return false;
            }

            RO.Util.DebugLog($"Successfully launched app: {packageActivityPath}");

            // The new process starts after profiling was enabled.
            unprofiledAppPid = -1;
            return true;
        }


        static public string GetExecutablePath(string bundleName)
        {
            if (string.IsNullOrEmpty(bundleName) || connectedDevices.Count == 0)
            {
                return "";
            }

            string outputString;
            string errorString;
            string command = $"shell cmd package resolve-activity --brief \"{bundleName}\"";

            adbTool.RunCommand(new string[] { "-s", connectedDevices[0], command }, PerfettoCapture, out outputString, out errorString);

            if (!string.IsNullOrEmpty(outputString))
            {
                string[] lines = outputString.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 1)
                {
                    string activityPath = lines[1].Trim();
                    return activityPath;
                }
            }

            return "";
        }

        static public void Init(string packageName = "", bool shimLayer = false, bool waitForGpuService = false)
        {
            if (!IsADBReady())
            {
                return;
            }

            // Refresh before arming so a cold editor session does not skip the device.
            connectedDevices = adbTool.GetDevices();

            string outputString;
            string errorString;
            if (connectedDevices.Count > 0)
            {
                if (ArmGpuProfiler(packageName) && waitForGpuService)
                {
                    WaitForGpuProfilingService();
                }

                if (shimLayer == true)
                {
                    adbTool.RunCommand(new string[] { "-s", connectedDevices[0], "shell setprop debug.oculus.forceVkShimLayer 1" }, PerfettoCapture, out outputString, out errorString);
                    RO.Util.DebugLog("forceVkShimLayer: " + outputString);
                    adbTool.RunCommand(new string[] { "-s", connectedDevices[0], "shell setprop debug.oculus.vkshim.forceVkLoadingFunctionShimming 1" }, PerfettoCapture, out outputString, out errorString);
                    RO.Util.DebugLog("forceVkLoadingFunctionShimming: " + outputString);
                }
                else
                {
                    adbTool.RunCommand(new string[] { "-s", connectedDevices[0], "shell setprop debug.oculus.forceVkShimLayer 0" }, PerfettoCapture, out outputString, out errorString);
                    // RO.Util.DebugLog("forceVkShimLayer: " + outputString);
                }
            }
            else
            {
                RO.Util.DebugLog("adb no adb device found");
            }
        }

        static public void StopPerfettoCapture()
        {
            try
            {
                List<string> devices = adbTool.GetDevices();
                if (devices.Count > 0)
                {
                    int commandResult = adbTool.RunCommand(new string[] { "-s", devices[0], "shell trigger_perfetto perf_optimizer_auto_stop" }, PerfettoCapture, out string outputString, out string errorString);
                    RO.Util.DebugLog("StopPerfettoCapture: " + outputString + " error: " + errorString.ToString());
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("Exception caught while stopping perfetto capture. Details  \r\n" + ex.Message.ToString() + "\r\n" + ex.StackTrace);
            }
        }

        /// <returns>False when no device could be resolved, so the caller skips pull and screenshot.</returns>
        static public bool IssuePerfettoCapture(bool screenshot, string packageName, bool lowOverhead = false)
        {
            RO.Util.DebugLog("Issue perfetto capture");

            if (adbTool == null)
            {
                UnityEngine.Debug.LogError("abd tool is not ready, did you call Init?");
                return false;
            }

            List<string> devices = adbTool.GetDevices();

            if (devices.Count == 0)
            {
                // Deliberately leaves connectedDevices alone. GetDevices cannot tell "no headset
                // attached" from "that adb call failed" -- it discards the exit code and returns an
                // empty list either way -- so publishing this result turns one bad round trip into
                // a phantom headset_disconnection that kills the capture from the editor's Update.
                UnityEngine.Debug.LogError(
                    "adb no adb device found; leaving the known device list untouched");
                return false;
            }

            connectedDevices = devices;
            PerfettoCaptureCommand(lowOverhead, packageName);
            return true;
        }

        private static void ClearPerfettoCapture()
        {
            string outputString;
            string errorString;
            // Directory exists, remove the file
            const string commandRemove = "shell rm " + outFile;
            adbTool.RunCommand(new string[] { "-s", connectedDevices[0], commandRemove }, PerfettoCapture, out outputString, out errorString);
            RO.Util.DebugLog("adb Perfetto device: " + outputString + " error: " + errorString.ToString());
            Thread.Sleep(2000);
        }

        /// <summary>Builds the adb shell command that arms GPU profiling for a package.</summary>
        /// <remarks>Split out from <see cref="ArmGpuProfiler"/> so the exact command is testable.</remarks>
        private static string BuildGpuProfilerArmCommand(string packageName)
        {
            string armTarget = string.IsNullOrEmpty(packageName) ? "" : $" {packageName}";
            return $"shell ovrgpuprofiler -e{armTarget}";
        }

        internal static string BuildGpuProfilerStatusCommand()
        {
            return "shell ovrgpuprofiler -i";
        }

        // Missing output is inconclusive, not evidence that profiling was disabled.
        internal static bool DetailedProfilingIsDisabled(string statusOutput)
        {
            return !string.IsNullOrEmpty(statusOutput) && statusOutput.Contains("is disabled");
        }

        /// <summary>
        /// Put the GPU driver in detailed profiling mode, starting the profiling service if it is
        /// not already up. Must run before any capture that reads GPU data, and before the app
        /// being analysed is launched.
        /// </summary>
        /// <remarks>
        /// gpuprofserver produces every GPU track in a capture. `gpuprofilingservice.rc` declares
        /// its init service `disabled`, and starts it at boot only when
        /// `persist.vr.gpuprofilingservice` is set, so on a normal headset it is simply not running
        /// after a reboot. Perfetto never starts it -- naming `gpu.renderstages.oculus` in the
        /// capture config while the service is down still records zero GPU slices -- so a capture
        /// taken in that state reaches the user as `GPU: 0.00ms`.
        ///
        /// Enabling is necessary but not sufficient, and the ordering is the whole game: detailed
        /// profiling is injected into a process at Vulkan/GL init, so `-e` "only applies to
        /// applications started after this mode is started". <see cref="Init"/> issues this before
        /// launching the app, which is the call that matters. Arming next to the capture, as this
        /// also does, cannot rescue a process that is already running -- it only leaves the device
        /// ready for the next launch.
        ///
        /// The status read before the enable is what lets the capture path tell the user which of
        /// those two situations it is in, rather than reporting an unexplained missing metric.
        /// </remarks>
        private static bool ArmGpuProfiler(string packageName)
        {
            if (adbTool == null || !adbTool.isReady || connectedDevices.Count == 0)
            {
                return false;
            }

            string outputString;
            string errorString;
            int statusCode = adbTool.RunCommand(
                new string[] { "-s", connectedDevices[0], BuildGpuProfilerStatusCommand() },
                PerfettoCapture, out outputString, out errorString, null, kGpuProfilerCommandTimeoutMs);
            if (statusCode == 0 && DetailedProfilingIsDisabled(outputString) && lastKnownPID != -1)
            {
                unprofiledAppPid = lastKnownPID;
            }

            int code = adbTool.RunCommand(
                new string[] { "-s", connectedDevices[0], BuildGpuProfilerArmCommand(packageName) },
                PerfettoCapture, out outputString, out errorString, null, kGpuProfilerCommandTimeoutMs);

            if (code != 0)
            {
                RO.Util.DebugLogError(
                    $"GPU profiler arm failed with exit {code}. " +
                    $"stdout: {outputString}; stderr: {errorString}");
                return false;
            }

            return true;
        }

        /// <summary>True only when getprop reported exactly "running", not "restarting".</summary>
        /// <remarks>Split out from the poll loop so the state matching is testable.</remarks>
        private static bool ServiceStateIsRunning(string getpropOutput)
        {
            if (string.IsNullOrEmpty(getpropOutput))
            {
                return false;
            }

            foreach (var line in getpropOutput.Split('\n'))
            {
                if (line.Trim() == "running")
                {
                    return true;
                }
            }

            return false;
        }

        private static void WaitForGpuProfilingService()
        {
            string outputString = null;
            string errorString;
            var deadline = DateTime.UtcNow.AddMilliseconds(kGpuServiceReadyTimeoutMs);

            while (true)
            {
                if (adbTool == null || !adbTool.isReady || connectedDevices.Count == 0)
                {
                    return;
                }

                // Bound each adb call by the remaining readiness budget.
                int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                {
                    ReportGpuServiceNotReady(outputString);
                    return;
                }

                int code = adbTool.RunCommand(
                    new string[] { "-s", connectedDevices[0], "shell getprop init.svc.gpuprofserver" },
                    PerfettoCapture, out outputString, out errorString, null,
                    Math.Min(kGpuProfilerCommandTimeoutMs, remainingMs));
                if (code != 0)
                {
                    RO.Util.DebugLogError(
                        "Could not read init.svc.gpuprofserver (exit " + code + "); this capture " +
                        "may have no GPU data. " + errorString);
                    return;
                }

                if (string.IsNullOrWhiteSpace(outputString))
                {
                    return;
                }

                if (ServiceStateIsRunning(outputString))
                {
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    ReportGpuServiceNotReady(outputString);
                    return;
                }

                Thread.Sleep(kGpuServiceReadyPollMs);
            }
        }

        /// <summary>Reads the gpuprofserver pid, or empty when it is not running.</summary>
        internal static string GpuServicePid()
        {
            if (adbTool == null || !adbTool.isReady || connectedDevices.Count == 0)
            {
                return string.Empty;
            }

            string outputString;
            string errorString;
            int code = adbTool.RunCommand(
                new string[] { "-s", connectedDevices[0], "shell pidof gpuprofserver" },
                PerfettoCapture, out outputString, out errorString, null, kGpuProfilerCommandTimeoutMs);

            return code == 0 && outputString != null ? outputString.Trim() : string.Empty;
        }

        /// <summary>True when a capture taken now cannot contain GPU data for the app.</summary>
        /// <remarks>
        /// Split out from <see cref="ReportIfAppPredatesGpuProfiler"/> so the rule is testable. It
        /// holds for as long as the un-instrumented process lives, not only for the capture that
        /// armed the device: the device reads as armed from the second capture onward while that
        /// process stays exactly as un-instrumented as it was.
        /// </remarks>
        internal static bool CaptureCannotHaveGpuData(int unprofiledPid, int appPid)
        {
            return appPid != -1 && appPid == unprofiledPid;
        }

        private static void ReportIfAppPredatesGpuProfiler()
        {
            if (!CaptureCannotHaveGpuData(unprofiledAppPid, lastKnownPID))
            {
                return;
            }

            RO.Util.DebugLogError(
                "GPU profiling was not enabled on this device until now, and the app was already " +
                "running. Detailed profiling only reaches an app that starts after it is enabled, " +
                "so this capture will have no GPU or MSAA data. Relaunch the app to collect them.");
        }

        /// <summary>Serialises the render stage trace against a capture.</summary>
        /// <remarks>
        /// A running <c>-t</c> takes the GPU profiling stream: one spanning a capture leaves the
        /// trace with zero GPU slices, and a second <c>-t</c> started while one is in flight fails
        /// outright with "Failed to start render stage tracing".
        /// </remarks>
        private static readonly object renderStagePrimeLock = new object();

        /// <summary>
        /// Runs a short render stage trace to completion so the app's own GPU profiling, which What
        /// If depends on, starts working. Must never overlap a capture.
        /// </summary>
        /// <remarks>
        /// Arming with <c>ovrgpuprofiler -e</c> only puts the GPU driver into detailed mode; the
        /// runtime's <c>StartGpuProfiling</c> still fails until a <c>-t</c> has run. Captures need a
        /// GPU session to have run only after the app stops a trace of its own, and get one from
        /// <see cref="FlushGpuStreamAfterAppTrace"/>.
        ///
        /// Every GPU session risks the Adreno GPU profiler layer crashing the app as the session
        /// starts (T291081389), so this runs once per launch and from nowhere else.
        /// </remarks>
        internal static void PrimeRenderStageStream()
        {
            lock (renderStagePrimeLock)
            {
                PrimeRenderStageStreamLocked();
            }
        }

        private static void PrimeRenderStageStreamLocked()
        {
            if (adbTool == null || !adbTool.isReady || connectedDevices.Count == 0)
            {
                return;
            }

            // The relaunched app holds no trace, and this run is a GPU session in its own right.
            appGpuTraceMayBeReleased = false;

            string outputString;
            string errorString;

            // -t=N is the duration form. A bare "-t N" is not: it parses as the flag with no
            // argument, silently runs the 0.1s default and leaves the N as a stray token.
            int code = adbTool.RunCommand(
                new string[] { "-s", connectedDevices[0], "shell ovrgpuprofiler -t=" + kRenderStagePrimeSeconds },
                PerfettoCapture, out outputString, out errorString, null, kGpuProfilerCommandTimeoutMs);

            RO.Util.DebugLog("adb ovrgpuprofiler -t : " + outputString);

            string combinedOutput = (outputString ?? string.Empty) + "\n" +
                (errorString ?? string.Empty);
            bool reportedStartFailure = combinedOutput.Contains("Failed to start render stage tracing");

            // The tool reports both of these and still exits 0, so neither is worth swallowing.
            if (code != 0 ||
                string.IsNullOrEmpty(outputString) ||
                outputString.Contains("No render stage data") ||
                reportedStartFailure)
            {
                // A warning, not an error: Bottleneck Analysis is unaffected, and the common cause
                // is benign -- the app has not started rendering yet -- so the message only has to
                // be visible enough to explain an empty What If.
                RO.Util.DebugLogWarning(
                    "Could not run the render stage trace What If analysis needs (exit " + code + "); " +
                    "What If may return no results until the app is relaunched. " + outputString + " " +
                    errorString);
            }
        }

        /// <summary>Call before asking the app for a quick perf.</summary>
        internal static void NoteQuickPerfRequested(bool frozen)
        {
            if (!frozen || appGpuTimeCachedPid == -1 || appGpuTimeCachedPid != lastKnownPID)
            {
                appGpuTraceMayBeReleased = true;
            }
        }

        internal static void NoteQuickPerfReceived(float gpuFrameTime)
        {
            if (gpuFrameTime > 0.0f)
            {
                appGpuTimeCachedPid = lastKnownPID;
            }
        }

        /// <summary>Call before starting What If, which traces render stages in the app.</summary>
        internal static void NoteAppGpuTraceStarted()
        {
            appGpuTraceMayBeReleased = true;
        }

        /// <summary>
        /// Runs a throwaway GPU session so the capture after it gets GPU data. Call with
        /// <see cref="renderStagePrimeLock"/> held.
        /// </summary>
        /// <remarks>
        /// When the app stops a render stage trace of its own, which it does when a capture starts
        /// and on unfreeze, the next GPU session on the device gets no GPU slices or surface
        /// workloads at all, however long it runs; the one after it is complete. Re-arming with
        /// <c>-e</c> or a session with only the profiling service enabled does not clear it.
        /// </remarks>
        private static void FlushGpuStreamAfterAppTrace()
        {
            if (!appGpuTraceMayBeReleased)
            {
                return;
            }
            appGpuTraceMayBeReleased = false;

            string outputString;
            string errorString;
            int code = adbTool.RunCommand(
                new string[] { "-s", connectedDevices[0], "shell perfetto -c - --txt -o " + gpuStreamFlushOutFile },
                PerfettoCapture, out outputString, out errorString, GpuStreamFlushConfig,
                kGpuProfilerCommandTimeoutMs);
            RO.Util.DebugLog("adb perfetto GPU stream flush: exit " + code + " " + outputString + " " + errorString);

            adbTool.RunCommand(
                new string[] { "-s", connectedDevices[0], "shell rm -f " + gpuStreamFlushOutFile },
                PerfettoCapture, out outputString, out errorString);
        }

        private static void ReportGpuServiceNotReady(string lastState)
        {
            RO.Util.DebugLogError(
                "GPU profiling service is not running after " + kGpuServiceReadyTimeoutMs +
                "ms; this capture will have no GPU data. init.svc.gpuprofserver = " +
                (string.IsNullOrEmpty(lastState) ? "<empty>" : lastState.Trim()));
        }

        private static void PerfettoCaptureCommand(bool lowOverhead, string packageName)
        {
            ClearPerfettoCapture();

            // Only the full capture reads GPU data; the low-overhead config has none. Init arms the
            // profiler too, but it can be skipped there, and this is the call site that actually
            // depends on it.
            if (!lowOverhead && ArmGpuProfiler(packageName))
            {
                WaitForGpuProfilingService();
                ReportIfAppPredatesGpuProfiler();
            }

            lock (renderStagePrimeLock)
            {
                if (!lowOverhead)
                {
                    FlushGpuStreamAfterAppTrace();
                }
                RunPerfettoCapture(lowOverhead, packageName);
            }
        }

        private static void RunPerfettoCapture(bool lowOverhead, string packageName)
        {
            string outputString;
            string errorString;

            const string commandCapture = "shell perfetto -c - --txt -o " + outFile;
            string finalConfig = captureConfig.Replace("{bundle_name}", packageName);
            string lowOverheadConfig = LowOverheadCaptureConfig.Replace("{bundle_name}", packageName);
            RO.Util.DebugLog("core sdk version 70+");

            int code2 = adbTool.RunCommand(new string[] { "-s", connectedDevices[0], commandCapture }, PerfettoCapture, out outputString, out errorString, lowOverhead ? lowOverheadConfig : finalConfig);
            if (code2 != 0)
            {
                RO.Util.DebugLogError(
                    $"Perfetto capture command returned exit {code2}. " +
                    $"stdout: {outputString}; stderr: {errorString}");
            }

        }

        public static bool ASWDetectionCommand()
        {
            string outputString = "";
            string errorString;
            const string commandCapture = "shell logcat -d | grep ASW";
            connectedDevices = adbTool.GetDevices();


            adbTool.RunCommand(new string[] { "-s", connectedDevices[0], commandCapture }, PerfettoCapture, out outputString, out errorString);
            RO.Util.DebugLog("adb ASW: " + outputString + " error: " + errorString);

            if (outputString.Contains("ASW="))
            {
                return true;
            }
            return false;
        }

        public static bool ExecuteADBCommand(string[] command, out string output)
        {
            string error;
            output = string.Empty;

            string commandStr = string.Join(" ", command);

            if (adbTool == null || !adbTool.isReady)
            {
                RO.Util.DebugLogError($"[ADB] OVRADBTool not ready, skipping: adb {commandStr}");
                return false;
            }

            int exitCode = adbTool.RunCommand(command, null, out output, out error);
            bool executed = exitCode == 0;

            if (!executed)
            {
                RO.Util.DebugLogError($"[ADB] adb {commandStr} failed with exit code {exitCode}. " +
                                      $"stdout: {output}; stderr: {error}");
            }
            else if (!string.IsNullOrEmpty(error))
            {
                RO.Util.DebugLogError($"[ADB] adb {commandStr} reported: {error}");
            }

            if (!executed)
            {
                output = error;
            }

            return executed;
        }

        public static void PullPerfettoCapture(string captureName)
        {
            RO.Util.DebugLog("Pulling Perfetto Capture");
            string outputString;
            string errorString;
            string outPath = GetOutputDirectory();
            string outFileName = captureName + ".ptrace";
            string finalPath = Path.Combine(outPath, outFileName);

            string tempFile = Path.Combine(Path.GetTempPath(), outFileName);
            string commandPull = string.Format("pull {0} {1}", outFile, tempFile);

            int exitCode = adbTool.RunCommand(new string[] { "-s", connectedDevices[0], commandPull }, PerfettoCapture, out outputString, out errorString, null, kFileTransferTimeoutMs);

            if (exitCode == 0 && File.Exists(tempFile))
            {
                try
                {
                    File.Copy(tempFile, finalPath, true);
                    File.Delete(tempFile);
                }
                catch (System.Exception ex)
                {
                    RO.Util.DebugLogError($"Failed to copy trace from temp to project: {ex.Message}");
                }
            }
            else
            {
                RO.Util.DebugLogError($"ADB pull failed (exit code {exitCode}): {errorString}");
            }
        }

        // Fail open when foreground state cannot be determined.
        static private bool IsPackageForeground(string packageName)
        {
            if (adbTool == null || !adbTool.isReady || string.IsNullOrEmpty(packageName))
            {
                return true;
            }

            string outputString = null;
            string errorString = null;
            int code;

            try
            {
                List<string> devices = adbTool.GetDevices();
                if (devices == null || devices.Count == 0)
                {
                    return true;
                }

                code = adbTool.RunCommand(
                    new string[] { "-s", devices[0],
                        "shell dumpsys activity activities | grep -m1 topResumedActivity" },
                    PerfettoCapture, out outputString, out errorString, null, kAdbCommandTimeoutMs);
            }
            catch (System.Exception ex)
            {
                RO.Util.DebugLog("Foreground check could not run, letting the capture through: " + ex.Message);
                return true;
            }

            RO.Util.DebugLog("adb topResumedActivity: " + outputString + " error: " + errorString);

            if (code != 0 || string.IsNullOrEmpty(outputString))
            {
                RO.Util.DebugLog(
                    "Foreground check inconclusive (exit " + code + "), letting the capture through");
                return true;
            }

            // Anchor on the component separator to avoid matching a package-name prefix.
            return outputString.Contains(packageName + "/");
        }

        private const string kGpuProfilerLayerLibrary = "libVkLayer_ADRENO_qprofiler.so";

        internal enum AppCrashKind
        {
            None,
            GpuProfilerLayer,
            Other,
        }

        /// <summary>Classifies the native crash, if any, that a crash log buffer records for the app.</summary>
        /// <remarks>
        /// crash_dump logs the crashing thread's backtrace under a
        /// <c>pid: N, tid: M, name: T  &gt;&gt;&gt; package &lt;&lt;&lt;</c> header, and the frames
        /// themselves carry crash_dump's pid, so they can only be tied to the app through that
        /// header. A frame in the Adreno profiler layer is the driver crash a GPU profiling session
        /// can set off when it starts against a running app; it kills the app, and the Editor only
        /// sees the connection drop.
        /// </remarks>
        internal static AppCrashKind ClassifyAppCrash(string crashLog, int appPid)
        {
            if (string.IsNullOrEmpty(crashLog) || appPid <= 0)
            {
                return AppCrashKind.None;
            }

            string backtraceHeader = "pid: " + appPid + ", tid: ";
            string fatalSignal = "pid " + appPid + " (";
            var kind = AppCrashKind.None;
            bool inAppBacktrace = false;

            foreach (var line in crashLog.Split('\n'))
            {
                if (line.Contains("*** *** ***"))
                {
                    inAppBacktrace = false;
                }
                else if (line.Contains(", tid: ") && line.Contains(">>> "))
                {
                    inAppBacktrace = line.Contains(backtraceHeader);
                    if (inAppBacktrace)
                    {
                        kind = AppCrashKind.Other;
                    }
                }
                else if (inAppBacktrace && line.Contains(kGpuProfilerLayerLibrary))
                {
                    return AppCrashKind.GpuProfilerLayer;
                }
                else if (line.Contains("Fatal signal") && line.Contains(fatalSignal))
                {
                    kind = AppCrashKind.Other;
                }
            }

            return kind;
        }

        private static AppCrashKind ReadAppCrashKind(int appPid)
        {
            if (adbTool == null || !adbTool.isReady || connectedDevices.Count == 0 || appPid <= 0)
            {
                return AppCrashKind.None;
            }

            string outputString;
            string errorString;
            try
            {
                int code = adbTool.RunCommand(
                    new string[] { "-s", connectedDevices[0], "logcat -d -b crash" },
                    PerfettoCapture, out outputString, out errorString, null, kAdbCommandTimeoutMs);
                return code == 0 ? ClassifyAppCrash(outputString, appPid) : AppCrashKind.None;
            }
            catch (System.Exception ex)
            {
                RO.Util.DebugLog("Could not read the device crash log: " + ex.Message);
                return AppCrashKind.None;
            }
        }

        private static void ReportCaptureAppNotForeground(string packageName, int appPid)
        {
            switch (ReadAppCrashKind(appPid))
            {
                case AppCrashKind.GpuProfilerLayer:
                    RO.Util.DebugLogError(
                        "Aborting capture: " + packageName + " (pid " + appPid + ") was killed by a " +
                        "crash in the headset's GPU profiler layer (" + kGpuProfilerLayerLibrary + ") " +
                        "when the capture started GPU profiling. This is a known GPU driver issue, " +
                        "not a bug in your app. Relaunch the app and capture again.");
                    break;
                case AppCrashKind.Other:
                    RO.Util.DebugLogError(
                        "Aborting capture: " + packageName + " (pid " + appPid + ") crashed during the " +
                        "capture. Its backtrace is in the device crash log (adb logcat -b crash).");
                    break;
                default:
                    RO.Util.DebugLogError(
                        "Aborting capture: " + packageName + " is not the foreground app, so the " +
                        "screenshot and trace would not be of the app being analysed");
                    break;
            }
        }

        static private void IssueScreenShotCapture(string outName)
        {
            if (adbTool == null)
            {
                UnityEngine.Debug.LogError("adb not found");
                return;
            }
            try
            {
                List<string> devices = adbTool.GetDevices();
                if (devices == null || devices.Count == 0)
                {
                    UnityEngine.Debug.LogError("adb no adb device found");
                    return;
                }
                const string outFile = "/sdcard/insight.png";
                string outputString;
                string errorString;
                string outPath = GetOutputDirectory();
                // Capture the screen on the device.
                string commandCapture = string.Format("-s {0} shell screencap -p {1}", devices[0], outFile);
                int code = adbTool.RunCommand(new string[] { "-s", devices[0], commandCapture }, PerfettoCapture, out outputString, out errorString);
                if (code == 0)
                {
                    string outFileName = outName + ".png";
                    string finalOutFile = string.Format("\"{0}\"", Path.Combine(outPath, outFileName));
                    string commandPull = string.Format("pull {0} {1}", outFile, finalOutFile);
                    RO.Util.DebugLog("adb screencap pull: " + commandPull);
                    int pullCode = adbTool.RunCommand(new string[] { "-s", devices[0], commandPull }, PerfettoCapture, out outputString, out errorString, null, kFileTransferTimeoutMs);
                    if (pullCode != 0)
                    {
                        RO.Util.DebugLogError($"adb screencap pull failed (exit code {pullCode}): {errorString}");
                    }
                    RO.Util.DebugLog("adb screencap pull: " + (outputString ?? "null"));
                    RO.Util.DebugLog("adb screencap pull: " + (errorString ?? "null"));
                }
                else
                {
                    UnityEngine.Debug.LogError("adb screencap device: " + code.ToString());
                    UnityEngine.Debug.LogError("adb screencap device: " + (outputString ?? "null"));
                    UnityEngine.Debug.LogError("adb screencap device: " + (errorString ?? "null"));
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("Exception caught while capturing screen on device. Details  \r\n" + ex.Message.ToString() + "\r\n" + ex.StackTrace);
            }
        }

        static public int GetAndroidOSVersion()
        {
            string output;
            if (CaptureTool.ExecuteADBCommand(new[] { "shell getprop ro.build.version.release" }, out output))
            {
                if (int.TryParse(output.Trim(), out int osVersion))
                {
                    return osVersion;
                }
                else
                {
                    RO.Util.DebugLog("Getting Android OS Version failed with error: " + output);
                }
            }
            return 0;
        }

        static public int GetHeadsetOSVersion()
        {
            string output;
            if (CaptureTool.ExecuteADBCommand(new[] { "shell getprop ro.vros.build.version" }, out output))
            {
                if (int.TryParse(output.Trim(), out int osVersion))
                {
                    return osVersion;
                }
                else
                {
                    RO.Util.DebugLog("Getting Quest OS Version failed with error: " + output);
                }
            }
            return 0;
        }

        /// <summary>
        /// Checks if the connected device is currently asleep (T241267036).
        /// Used by the async capture thread to detect device sleep and abort early,
        /// preventing blank screenshots from being captured.
        /// </summary>
        static public bool IsDeviceAsleep()
        {
            string output;
            if (ExecuteADBCommand(new[] { "shell dumpsys power | grep \"mWakefulness=\"" }, out output))
            {
                if (!string.IsNullOrEmpty(output) && output.Contains("Asleep"))
                {
                    return true;
                }
            }
            return false;
        }

        static public int CompareStringsAsHex(string filePath1, string filePath2)
        {
            string fileName1 = Path.GetFileNameWithoutExtension(filePath1);
            string fileName2 = Path.GetFileNameWithoutExtension(filePath2);
            bool valid1 = ulong.TryParse(fileName1, System.Globalization.NumberStyles.HexNumber, null, out ulong l1);
            bool valid2 = ulong.TryParse(fileName2, System.Globalization.NumberStyles.HexNumber, null, out ulong l2);
            if (!valid1 && !valid2) return string.Compare(fileName1, fileName2, StringComparison.Ordinal);
            if (!valid1) return 1;
            if (!valid2) return -1;
            return (int)(l2 - l1);
        }

        static public string[] GetCapturedList()
        {
            var outPath = GetOutputDirectory();
            // RO.Util.DebugLog("GetCapturedList: " + outPath);
            string[] dirs = Directory.GetFiles(outPath, "*.ptrace");
            Array.Sort(dirs, CompareStringsAsHex);
            return dirs;
        }

        static private void ProcessCaptureToJsonObjFunc(object filePathObj)
        {
            string filePath = (string)filePathObj;
            RO.Util.DebugLog("Async Loading: " + filePath);
            string finalInFile = string.Format("\"{0}\"", filePath);
            MetricAPI.GetCaptureMetric(finalInFile, GetMetricJsonOutputPath(filePath));
            lock (lockObj)
            {
                metricProcessWorkInQueue--;
            }
        }

        static public int CaptureToJsonObjAsyncCount()
        {
            return metricProcessWorkInQueue;
        }

        static public int CaptureToJsonAsyncTimeout()
        {
            lock (lockObj)
            {
                metricProcessWorkInQueue--;
            }
            return metricProcessWorkInQueue;
        }

        static public void ProcessCaptureToJsonObjAsync(string filePath)
        {
            lock (lockObj)
            {
                metricProcessWorkInQueue++;
                ThreadPool.QueueUserWorkItem(ProcessCaptureToJsonObjFunc, filePath);
            }
            RO.Util.DebugLog("Async ProcessCaptureToJsonObjAsync: " + filePath);
        }

        static public void IssuePerfettoCaptureAsync(bool screenshot, string packageName, string captureName, bool lowOverhead = false, Action onFirstExecute = null, Action onFinishedScreenshot = null)
        {
            if (captureThread != null && captureThread.IsAlive)
            {
                captureThread.Abort();
                captureThread.Join();
            }

            captureThread = new Thread(() =>
            {
                // The main thread re-resolves the PID once a dead app cancels the capture.
                int appPid = lastKnownPID;
                if (onFirstExecute != null)
                {
                    onFirstExecute();
                }
                // No device means no trace was written, so pulling one would read whatever the
                // previous capture left on the device -- and PullPerfettoCapture indexes
                // connectedDevices[0], which is empty in exactly this case.
                if (!IssuePerfettoCapture(screenshot, packageName, lowOverhead))
                {
                    if (onFinishedScreenshot != null)
                    {
                        onFinishedScreenshot();
                    }
                    return;
                }

                bool deviceAsleep = IsDeviceAsleep();
                if (deviceAsleep)
                {
                    RO.Util.DebugLogError("Device entered sleep mode during Perfetto capture - aborting to prevent blank frame");
                    if (onFinishedScreenshot != null)
                    {
                        onFinishedScreenshot();
                    }
                    return;
                }

                if (!IsPackageForeground(packageName))
                {
                    ReportCaptureAppNotForeground(packageName, appPid);
                    if (onFinishedScreenshot != null)
                    {
                        onFinishedScreenshot();
                    }
                    return;
                }

                PullPerfettoCapture(captureName);
                if (screenshot)
                {
                    IssueScreenShotCapture(captureName);
                }
                if (onFinishedScreenshot != null)
                {
                    onFinishedScreenshot();
                }
            });
            captureThread.Start();
        }

        /// <summary>
        /// Captures only a screenshot without any Perfetto trace capture.
        /// This is useful for freeze frame scenarios where you only need a visual thumbnail.
        /// </summary>
        /// <returns>True if capture was started, false if a capture is already in progress</returns>
        static public void IssueScreenShotCaptureAsync(string captureName, Action onFirstExecute = null, Action onFinished = null)
        {
            if (captureThread != null && captureThread.IsAlive)
            {
                captureThread.Abort();
                captureThread.Join();
            }

            captureThread = new Thread(() =>
            {
                if (onFirstExecute != null)
                {
                    onFirstExecute();
                }

                // Check if device is asleep before taking screenshot (T241267036)
                if (IsDeviceAsleep())
                {
                    RO.Util.DebugLogError("Device is asleep - skipping screenshot capture to prevent blank frame");
                }
                else
                {
                    IssueScreenShotCapture(captureName);
                }

                if (onFinished != null)
                {
                    onFinished();
                }
            });
            captureThread.Start();
        }
        static public bool IsFileLocked(string filePath)
        {
            try
            {
                using (FileStream fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    // If the file is not locked, the code above will reach here
                    return false; // File is not locked
                }
            }
            catch (IOException)
            {
                // Check if the exception is related to a file lock
                return true; // File is locked
            }
        }
    }
}
