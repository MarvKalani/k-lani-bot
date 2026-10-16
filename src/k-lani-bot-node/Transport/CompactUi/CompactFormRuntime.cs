namespace KLaniBot.Transport.CompactUi;

// Form orchestration refreshes the operated node before each input and keeps all state private and instance-bound.
public sealed class CompactFormRuntime(ICompactFormBackend backend, TimeProvider? timeProvider = null)
{
    public const int MaximumForms = 32;
    public const int MaximumFields = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<CompactFormReference, CompactFormState> _forms = new();
    private readonly LinkedList<CompactFormReference> _order = new();
    private readonly CompactFormPager _pages = new();

    public int FormCount => _forms.Count;

    public async Task<CompactFormAttempt<CompactFormState>> ReadAsync(int processId, CompactSemanticTarget scope,
        IReadOnlyList<string>? requestedFields, CancellationToken cancellation)
    {
        Expire();
        if (scope == null || processId < 1 || scope.ProcessId != processId || !IsContainer(scope.Role.Name))
        {
            return new(null, CompactFormError.InvalidArguments, "Select an observed form container.");
        }
        var candidate = new CompactFormState(processId, scope);
        CompactFormState? form = _forms.Values.FirstOrDefault(form => form.Key == candidate.Key);
        if (form == null)
        {
            while (_forms.Count >= MaximumForms)
            {
                Remove(_order.First!.Value);
            }
            form = candidate;
            _forms.Add(form.Reference, form);
            _order.AddLast(form.Reference);
        }
        CompactFormAttempt<CompactFormState> refreshed = await RefreshAsync(form, cancellation);
        if (refreshed.Error != null)
        {
            return refreshed;
        }
        if (requestedFields != null && requestedFields.Any(name => form.Fields.Count(field => CompactFormFields.Matches(field, name)) != 1))
        {
            return new(null, CompactFormError.RequestedFieldUnknownOrAmbiguous);
        }

        return new(form, null);
    }

    public async Task<CompactFormAttempt<CompactFormState>> CurrentAsync(CompactFormReference reference, string expectedContext,
        CancellationToken cancellation)
    {
        Expire();
        if (!_forms.TryGetValue(reference, out CompactFormState? form))
        {
            return new(null, CompactFormError.UnknownOrExpiredForm);
        }
        CompactFormAttempt<CompactFormState> refreshed = await RefreshAsync(form, cancellation);
        if (refreshed.Error != null)
        {
            return refreshed;
        }

        return form.ContextRevision == expectedContext ? new(form, null) : new(null, CompactFormError.StaleContext);
    }

    public async Task<CompactFormAttempt<CompactFormSetResult>> SetAsync(CompactFormReference reference, string expectedContext,
        CompactFormFieldReference fieldReference, CompactFormValue value, CancellationToken cancellation)
    {
        CompactFormAttempt<CompactFormState> current = await CurrentAsync(reference, expectedContext, cancellation);
        if (current.Error != null)
        {
            return new(null, current.Error, current.Detail);
        }
        CompactFormState form = current.Value!;
        CompactFormField? field = form.Fields.FirstOrDefault(field => field.Reference == fieldReference);
        if (field == null)
        {
            return new(null, CompactFormError.UnknownOrStaleField);
        }
        if (!CompactFormPolicy.TryPrepareSet(field, value, form.SnapshotId, out var prepared))
        {
            return new(null, prepared.Error, prepared.Detail);
        }

        return await ExecuteSetAsync(form, field, prepared.Value!, expectedContext, cancellation);
    }

    public async Task<CompactFormAttempt<CompactFormOptionsResult>> OptionsAsync(CompactFormReference reference, string expectedContext,
        CompactFormFieldReference fieldReference, CompactFormPageOptions page, CancellationToken cancellation)
    {
        if (page == null || page.Limit is < 1 or > CompactFormPager.MaximumPageSize)
        {
            return new(null, CompactFormError.InvalidArguments);
        }
        CompactFormAttempt<CompactFormState> current = await CurrentAsync(reference, expectedContext, cancellation);
        if (current.Error != null)
        {
            return new(null, current.Error, current.Detail);
        }
        CompactFormState form = current.Value!;
        CompactFormField? field = form.Fields.FirstOrDefault(candidate => candidate.Reference == fieldReference);
        if (field == null)
        {
            return new(null, CompactFormError.UnknownOrStaleField);
        }
        if (field.Kind != CompactFormFieldKind.Choice || field.Metadata.Choices.Support != Platforms.UiFormSupport.Available)
        {
            return new(null, CompactFormError.OptionsUnsupported);
        }
        bool opened = field.Metadata.Choices.NeedsOpen && page.Cursor == null;
        if (opened)
        {
            CompactFormAttempt<CompactFormField> opening = await OpenOptionsAsync(form, field, cancellation);
            if (opening.Error != null)
            {
                return new(new(form, field, new([], false, null), false, true, false, opening.Detail), opening.Error, opening.Detail);
            }
            field = opening.Value!;
        }
        string binding = OptionsBinding(form, field);
        if (!_pages.TryPage(binding, field.Options, page, form.Reference, out var paged))
        {
            return new(null, paged.Error);
        }

        return new(new(form, field, paged.Value!, field.Metadata.Choices.Complete, opened, true), null);
    }

    private async Task<CompactFormAttempt<CompactFormField>> OpenOptionsAsync(CompactFormState form, CompactFormField field,
        CancellationToken cancellation)
    {
        if (!CompactFormPolicy.Writable(field, false))
        {
            return new(null, CompactFormError.UnsupportedField);
        }
        try
        {
            var open = new CompactFormMutation(Actions.Tool.ClickElement, field.NativeId, form.SnapshotId,
                CompactFormValueKind.Option, field.Value ?? "", OpenOnly: true);
            CompactFormDispatch response = await backend.DispatchAsync(open, cancellation);
            if (!response.Success)
            {
                return new(null, CompactFormError.Unconfirmed, response.Error);
            }
            CompactFormAttempt<CompactFormState> refreshed = await RefreshAsync(form, cancellation);
            CompactFormField? actual = form.Fields.FirstOrDefault(current => current.Reference == field.Reference);

            return refreshed.Error != null || actual == null || actual.Value != field.Value
                ? new(null, CompactFormError.Unconfirmed, "Opening changed or lost selection.") : new(actual, null);
        }
        catch (Exception exception)
        {
            return new(null, CompactFormError.Unconfirmed, exception.Message);
        }
    }

    public bool TryFieldPage(CompactFormState form, IReadOnlyList<string>? requested, CompactFormPageOptions options,
        out CompactFormAttempt<CompactFormPage<CompactFormField>> attempt)
    {
        if (requested != null && requested.Any(name => form.Fields.Count(field => CompactFormFields.Matches(field, name)) != 1))
        {
            attempt = new(null, CompactFormError.RequestedFieldUnknownOrAmbiguous);
            return false;
        }
        CompactFormField[] selected = form.Fields.Where(field => requested == null
            || requested.Any(name => CompactFormFields.Matches(field, name))).ToArray();
        var binding = new System.Text.Json.Nodes.JsonObject
        {
            ["form"] = form.Reference.Value.ToString(),
            ["context"] = form.ContextRevision
        };
        if (requested != null)
        {
            binding["fields"] = new System.Text.Json.Nodes.JsonArray(requested.Select(name => (System.Text.Json.Nodes.JsonNode?)
                System.Text.Json.Nodes.JsonValue.Create(name)).ToArray());
        }

        return _pages.TryPage("fields/" + CompactJsonText.Serialize(binding), selected, options, form.Reference, out attempt);
    }

    private static string OptionsBinding(CompactFormState form, CompactFormField field)
    {
        return CompactJsonText.Serialize(new System.Text.Json.Nodes.JsonObject
        {
            ["form"] = form.Reference.Value.ToString(),
            ["field"] = field.Reference.Value.ToString(),
            ["context"] = form.ContextRevision,
            ["options"] = field.OptionsRevision
        });
    }

    private async Task<CompactFormAttempt<CompactFormSetResult>> ExecuteSetAsync(CompactFormState form, CompactFormField field,
        CompactFormMutation mutation, string checkedContext, CancellationToken cancellation)
    {
        try
        {
            CompactFormDispatch dispatched = await backend.DispatchAsync(mutation, cancellation);
            if (!dispatched.Success)
            {
                return Unconfirmed(form, field, checkedContext, dispatched.Error ?? "Node operation is unconfirmed.");
            }
            CompactFormAttempt<CompactFormState> refreshed = await RefreshAsync(form, cancellation);
            CompactFormField? actual = form.Fields.FirstOrDefault(current => current.Reference == field.Reference);
            if (refreshed.Error != null || actual == null || actual.Value != mutation.Expected)
            {
                return Unconfirmed(form, field, checkedContext, refreshed.Detail
                    ?? "Full native field value did not match the requested input.");
            }
            return new(new(true, true, form.ContextRevision, form.StructureRevision, field.Reference,
                CompactFormPolicy.Invalidated(field, form.Fields), field.Metadata.Source,
                field.Metadata.Dependencies != null, checkedContext), null);
        }
        catch (Exception exception)
        {
            return Unconfirmed(form, field, checkedContext, exception.Message);
        }
    }

    private static CompactFormAttempt<CompactFormSetResult> Unconfirmed(CompactFormState form, CompactFormField field,
        string context, string error)
    {
        return new(new(false, false, form.ContextRevision, form.StructureRevision, field.Reference, [],
            field.Metadata.Source, field.Metadata.Dependencies != null, context, error), CompactFormError.Unconfirmed, error);
    }

    private async Task<CompactFormAttempt<CompactFormState>> RefreshAsync(CompactFormState form, CancellationToken cancellation)
    {
        try
        {
            return await ObserveAsync(form, cancellation);
        }
        catch (Exception exception)
        {
            return new(null, CompactFormError.ObservationUnavailable, exception.Message);
        }
    }

    private async Task<CompactFormAttempt<CompactFormState>> ObserveAsync(CompactFormState form, CancellationToken cancellation)
    {
        if (form.Scope.WindowHandle is not long windowHandle)
        {
            return new(null, CompactFormError.ScopeChanged);
        }
        CompactFormSnapshot snapshot = await backend.RefreshAsync(form.ProcessId, windowHandle, cancellation);
        string previousContext = form.ContextRevision;
        if (!form.TryRefresh(snapshot, out var attempt))
        {
            return attempt;
        }
        form.Touched = _time.GetUtcNow();
        if (previousContext.Length > 0 && previousContext != form.ContextRevision)
        {
            _pages.Drop(form.Reference);
        }

        return new(form, null);
    }

    public void Expire()
    {
        foreach (CompactFormState form in _forms.Values.ToArray())
        {
            if (_time.GetUtcNow() - form.Touched >= Lifetime)
            {
                Remove(form.Reference);
            }
        }
    }

    private void Remove(CompactFormReference reference)
    {
        _forms.Remove(reference);
        _order.Remove(reference);
        _pages.Drop(reference);
    }

    public void Clear()
    {
        _forms.Clear();
        _order.Clear();
        _pages.Clear();
    }

    private static bool IsContainer(string role)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(role, "Window|frame|dialog|document|group|panel|section",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
