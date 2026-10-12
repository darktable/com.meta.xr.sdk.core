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

using Meta.XR.Json;

namespace Meta.XR.AI.AgentBridge
{
    /// <summary>What one <c>muse exec --json</c> event line means to the chat bridge.</summary>
    internal enum MuseExecEventKind
    {
        /// <summary>No renderable content (bookkeeping/lifecycle line); a session id may still be present.</summary>
        None,

        /// <summary>Incremental assistant text (<c>run.output.delta</c>).</summary>
        AssistantDelta,

        /// <summary>The run finished successfully (<c>payload_type</c> == <c>run.terminal.completed</c>); <see cref="MuseExecEvent.Text"/> is the full answer.</summary>
        TurnCompleted,

        /// <summary>The run ended in a non-success terminal state; <see cref="MuseExecEvent.Error"/> carries the reason.</summary>
        TurnFailed,
    }

    /// <summary>The parsed meaning of a single <c>muse exec --json</c> stdout line.</summary>
    internal struct MuseExecEvent
    {
        public MuseExecEventKind Kind;
        public string? Text;

        /// <summary>Non-null when this line carried the session id (it rides on the session stream).</summary>
        public string? SessionId;

        /// <summary>Set only when <see cref="Kind"/> is <see cref="MuseExecEventKind.TurnFailed"/>.</summary>
        public string? Error;
    }

    /// <summary>
    /// Translates one <c>muse exec --json</c> JSONL event into a <see cref="MuseExecEvent"/>.
    /// Pure and line-oriented (mirrors AcpParser) so the mapping is unit-testable without spawning
    /// the CLI. Unknown or malformed lines yield <see cref="MuseExecEventKind.None"/> rather than
    /// throwing, so a future event type can't break the stream reader.
    /// </summary>
    internal static class MuseExecJsonParser
    {
        public static MuseExecEvent Parse(string line)
        {
            var result = new MuseExecEvent { Kind = MuseExecEventKind.None };
            if (string.IsNullOrWhiteSpace(line))
                return result;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(line);
            }
            catch
            {
                return result;
            }
            if (root == null)
                return result;

            // The session id rides on the top-level session stream; capture it regardless of the
            // payload so the caller can persist it for `--session-id` resume.
            var stream = root["stream"];
            if (stream?["kind"]?.Value<string>() == "session")
                result.SessionId = stream["id"]?.Value<string>();

            var payloadType = root["payload_type"]?.Value<string>();
            var payload = root["payload"];

            if (payloadType == "run.output.delta")
            {
                result.Kind = MuseExecEventKind.AssistantDelta;
                result.Text = payload?["text"]?.Value<string>() ?? string.Empty;
            }
            else if (payloadType != null && payloadType.StartsWith("run.terminal."))
            {
                if (payloadType == "run.terminal.completed")
                {
                    result.Kind = MuseExecEventKind.TurnCompleted;
                    result.Text = payload?["text"]?.Value<string>() ?? string.Empty;
                }
                else
                {
                    result.Kind = MuseExecEventKind.TurnFailed;
                    var reason = payload?["reason"]?.Value<string>();
                    var terminal = payload?["terminal"]?.Value<string>();
                    result.Error = !string.IsNullOrEmpty(reason)
                        ? reason
                        : $"Muse Code run ended with status '{terminal ?? payloadType}'.";
                }
            }

            return result;
        }
    }
}
