using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

namespace KLaniBot.Transport;

// The three-tool cartridge gateway delegates only the selected program's generated branch to native compact services after
// fresh Info binding.
internal sealed class GeneratedCartridgeSession : IDisposable
{
    private readonly GatewayProgramCartridge _program;
    private readonly CompactGatewaySession _compact;
    private int? _process;

    public GeneratedCartridgeSession(GatewayProgramCartridge program, NodeTransportClient client, string node, string library,
        Func<string> protocol)
    {
        _program = program;
        _compact = new(new CompactRegisteredNodeClient(client), node, new(library, null), protocol);
    }

    // Throws ArgumentException for ambiguous/mismatching actual program selection; native failures remain explicit failed results.
    public async Task<JsonObject> InfoAsync(JsonObject arguments, CancellationToken cancellation)
    {
        _process = null;
        var info = await _compact.CallAsync("ui_info", new(), cancellation);
        if (info["success"]?.ToString() != "true")
        {
            return info;
        }
        int? requested = arguments["pid"]?.GetValue<int>();
        var windows = info["windows"]!.AsArray().OfType<JsonObject>().Where(window =>
            window["process_name"]?.ToString() == _program.ProcessName && window["version"]?.ToString() == _program.Identity.Build
            && (requested == null || window["pid"]!.GetValue<int>() == requested)).ToArray();
        var processes = windows.Select(window => window["pid"]!.GetValue<int>()).Distinct().ToArray();
        if (processes.Length != 1)
        {
            throw new ArgumentException("Select one matching PID; absent or ambiguous builds cannot bind generated functions.");
        }
        var observed = await _compact.CallAsync("ui_observe", new JsonObject { ["pid"] = processes[0] }, cancellation);
        if (observed["success"]?.ToString() != "true")
        {
            return observed;
        }
        _process = processes[0];

        return new JsonObject
        {
            ["success"] = true,
            ["pid"] = _process,
            ["application"] = _program.Application,
            ["build"] = _program.Identity.Build,
            ["generated_revision"] = _program.Generated?.Revision,
            ["categories"] = new JsonArray("Administration", "Use", "Info", "Programming")
        };
    }

    // Throws ArgumentException before generated schema/input access without the selected program's fresh Info binding.
    public async Task<JsonObject> CallAsync(string tool, JsonObject arguments, CancellationToken cancellation)
    {
        if (_process == null)
        {
            throw new ArgumentException("Call cartridge_info before loading or executing generated functions.");
        }
        var routed = arguments.DeepClone().AsObject();
        routed.Remove("node");
        if (tool == "cartridge_browse")
        {
            routed["topic"] = routed["path"]?.DeepClone();
            routed.Remove("path");
        }
        if (tool == "cartridge_call")
        {
            var values = routed["arguments"] as JsonObject ?? new JsonObject();
            if (values["pid"] != null && values["pid"]!.GetValue<int>() != _process)
            {
                throw new ArgumentException("Generated function PID differs from the selected program; refresh Info.");
            }
            string function = routed["function"]!.GetValue<string>();
            if (CompactGatewayFunctionTools.Find(function) == null || function == "Info.preflight")
            {
                values["pid"] = _process;
            }
            if (!routed.ContainsKey("arguments"))
            {
                routed["arguments"] = values;
            }
        }

        return await _compact.CallAsync(tool, routed, cancellation);
    }

    public void Dispose()
    {
        _compact.Dispose();
    }
}
