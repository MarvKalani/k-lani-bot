using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Routed form commands preserve finite validation failures, bounded field/option pages and full postconditions before reply projection.
public sealed class CompactGatewayForms
{
    private readonly CompactFormRuntime _runtime;
    private readonly CompactGatewayObservation _observation;
    private readonly CompactGatewayState _state;

    public CompactGatewayForms(CompactNativeFormBackend backend, CompactGatewayObservation observation, CompactGatewayState state)
    {
        _runtime = new(backend);
        _observation = observation;
        _state = state;
    }

    // Throws CompactNodeException for missing observed scope; invalid arguments/state return explicit non-mutating refusals.
    public async Task<JsonObject> CallAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        if (!CompactFormCodec.TryRead(name, arguments, out var parsed))
        {
            return Failure(parsed.Error!.Value, parsed.Detail);
        }
        var command = parsed.Value!;
        switch (command.Kind)
        {
            case CompactFormCommandKind.Read:
                return await ReadAsync(command, cancellation);
            case CompactFormCommandKind.Options:
                return await OptionsAsync(command, cancellation);
            case CompactFormCommandKind.Set:
                return await SetAsync(command, cancellation);
            case CompactFormCommandKind.Validate:
                return await ValidateAsync(command, cancellation);
            default:
                throw new UnreachableException();
        }
    }

    // Throws CompactNodeException when the supplied scope is not a published reference bound to its original current window.
    private async Task<JsonObject> ReadAsync(CompactFormCommand command, CancellationToken cancellation)
    {
        var scope = await _observation.TargetAsync(command.Scope!, command.ProcessId!.Value, cancellation);
        var read = await _runtime.ReadAsync(command.ProcessId.Value, scope, command.Fields, cancellation);
        if (read.Error != null)
        {
            return Failure(read.Error.Value, read.Detail);
        }
        var reply = new CompactFormResponses(_runtime, _state.Results, _state.Authority).Read(read.Value!, command.Fields, command.Page);
        if (reply.Error != null)
        {
            return Failure(reply.Error.Value);
        }
        if (reply.EvidenceError != null)
        {
            return new JsonObject { ["success"] = false, ["error"] = CompactResultErrors.ToWireMessage(reply.EvidenceError.Value) };
        }
        _state.SelectedProcess = command.ProcessId;

        return reply.Value!;
    }

    private async Task<JsonObject> OptionsAsync(CompactFormCommand command, CancellationToken cancellation)
    {
        var result = await _runtime.OptionsAsync(command.Form!, command.Context!, command.Field!, command.Page, cancellation);
        if (result.Value != null)
        {
            return CompactFormResponses.Options(result.Value);
        }

        return Failure(result.Error!.Value, result.Detail);
    }

    private async Task<JsonObject> SetAsync(CompactFormCommand command, CancellationToken cancellation)
    {
        var result = await _runtime.SetAsync(command.Form!, command.Context!, command.Field!, command.Value!, cancellation);
        if (result.Value != null)
        {
            return CompactFormResponses.Set(command.Form!, result.Value);
        }

        return Failure(result.Error!.Value, result.Detail);
    }

    private async Task<JsonObject> ValidateAsync(CompactFormCommand command, CancellationToken cancellation)
    {
        var current = await _runtime.CurrentAsync(command.Form!, command.Context!, cancellation);
        if (current.Error != null)
        {
            return Failure(current.Error.Value, current.Detail);
        }

        return CompactFormWire.Validation(current.Value!, CompactFormPolicy.Validate(current.Value!.Fields));
    }

    private static JsonObject Failure(CompactFormError error, string? detail = null)
    {
        return new JsonObject { ["success"] = false, ["error"] = detail ?? CompactFormNames.Error(error) };
    }

    public void Clear()
    {
        _runtime.Clear();
    }
}
