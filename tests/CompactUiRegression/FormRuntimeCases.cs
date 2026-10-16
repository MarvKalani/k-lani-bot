using KLaniBot.Platforms;
using KLaniBot.Transport.CompactUi;

// Native form orchestration is exercised against deterministic node snapshots; these checks claim no installed-application acceptance.
internal static class FormRuntimeCases
{
    public static async Task RunAsync()
    {
        await VerifiedSetAndRefusalAsync();
        await UnknownAndIgnoredAsync();
        await OptionsAndContextPagingAsync();
        await InstanceAndExpiryAsync();
        await OpeningFailuresAsync();
        await FullValueEvidenceAsync();
        await CapacityAsync();
        await ReferenceAuthorityAsync();
    }

    private static async Task VerifiedSetAndRefusalAsync()
    {
        var backend = new FixtureBackend();
        var runtime = new CompactFormRuntime(backend);
        var first = await runtime.ReadAsync(10, Scope(), null, CancellationToken.None);
        Require(first.Error == null, "read");
        CompactFormState form = first.Value!;
        CompactFormField choice = form.Fields[0];
        CompactFormField hidden = form.Fields[1];
        var denied = await runtime.SetAsync(form.Reference, form.ContextRevision, hidden.Reference,
            new(CompactFormValueKind.Text, Text: "not sent"), CancellationToken.None);
        Require(denied.Error == CompactFormError.UnsupportedField && backend.Mutations == 0, "hidden field");
        var selected = await runtime.SetAsync(form.Reference, form.ContextRevision, choice.Reference,
            new(CompactFormValueKind.Option, OptionId: "Wide", OptionsRevision: choice.OptionsRevision), CancellationToken.None);
        Require(selected.Value!.Verified && backend.Mutations == 1, "verified native option effect");
        Require(form.Fields[0].Reference == choice.Reference, "stable field reference");
        var before = form.ContextRevision;
        backend.ExternalChange();
        var stale = await runtime.SetAsync(form.Reference, before, choice.Reference,
            new(CompactFormValueKind.Option, OptionId: "Basic", OptionsRevision: choice.OptionsRevision), CancellationToken.None);
        Require(stale.Error == CompactFormError.StaleContext && backend.Mutations == 1, "stale context before input");
    }

    private static async Task UnknownAndIgnoredAsync()
    {
        var backend = new FixtureBackend { Required = null };
        var runtime = new CompactFormRuntime(backend);
        CompactFormState form = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        CompactFormValidation unknown = CompactFormPolicy.Validate(form.Fields);
        Require(!unknown.Ready && unknown.UnknownConstraints.Count == 1, "unknown stays unknown");
        backend.Required = true;
        await runtime.ReadAsync(10, Scope(), null, CancellationToken.None);
        backend.IgnoreWrites = true;
        CompactFormField field = form.Fields[0];
        var ignored = await runtime.SetAsync(form.Reference, form.ContextRevision, field.Reference,
            new(CompactFormValueKind.Option, OptionId: "Wide", OptionsRevision: field.OptionsRevision), CancellationToken.None);
        Require(ignored.Error == CompactFormError.Unconfirmed && ignored.Value!.Verified == false, "unconfirmed full postcondition");
        Require(backend.Mutations == 1, "no automatic retry");
        backend.ReadOnly = true;
        await runtime.ReadAsync(10, Scope(), null, CancellationToken.None);
        var readOnly = await runtime.SetAsync(form.Reference, form.ContextRevision, field.Reference,
            new(CompactFormValueKind.Option, OptionId: "Wide", OptionsRevision: field.OptionsRevision), CancellationToken.None);
        Require(readOnly.Error == CompactFormError.UnsupportedField && backend.Mutations == 1, "read-only refusal");
    }

    private static async Task OptionsAndContextPagingAsync()
    {
        var backend = new FixtureBackend { ManyOptions = true };
        var runtime = new CompactFormRuntime(backend);
        CompactFormState form = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        CompactFormField field = form.Fields[0];
        string context = form.ContextRevision;
        var first = await runtime.OptionsAsync(form.Reference, context, field.Reference, new(Limit: 100), CancellationToken.None);
        Require(first.Value!.Page.Items.Count == 100 && !first.Value.Page.Complete, "first option page");
        var last = await runtime.OptionsAsync(form.Reference, context, field.Reference,
            new(first.Value.Page.NextCursor, 100), CancellationToken.None);
        Require(last.Value!.Page.Items.Count == 50 && last.Value.Page.Complete, "complete options");
        var pending = await runtime.OptionsAsync(form.Reference, context, field.Reference, new(Limit: 100), CancellationToken.None);
        backend.ExternalChange();
        var stale = await runtime.OptionsAsync(form.Reference, context, field.Reference,
            new(pending.Value!.Page.NextCursor, 100), CancellationToken.None);
        Require(stale.Error == CompactFormError.StaleContext && backend.Mutations == 0, "external change before page");
    }

    private static async Task InstanceAndExpiryAsync()
    {
        var backend = new FixtureBackend();
        var clock = new MutableClock();
        var runtime = new CompactFormRuntime(backend, clock);
        CompactFormState form = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        clock.Advance(CompactFormRuntime.Lifetime);
        Require((await runtime.CurrentAsync(form.Reference, form.ContextRevision, CancellationToken.None)).Error
            == CompactFormError.UnknownOrExpiredForm, "exact TTL");
        form = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        backend.Started = "replacement";
        Require((await runtime.CurrentAsync(form.Reference, form.ContextRevision, CancellationToken.None)).Error
            == CompactFormError.ScopeChanged, "new PID/window lifetime");
        runtime.Clear();
        Require(runtime.FormCount == 0 && backend.Mutations == 0, "clear without input");
    }

    private static CompactSemanticTarget Scope()
    {
        return new(10, "Door", CompactRole.Parse("Window"), "", CompactWindowKind.Main, [], 1, "start");
    }

    private static async Task OpeningFailuresAsync()
    {
        var backend = new FixtureBackend { NeedsOpen = true };
        var runtime = new CompactFormRuntime(backend);
        CompactFormState form = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        CompactFormField field = form.Fields[0];
        var badPage = await runtime.OptionsAsync(form.Reference, form.ContextRevision, field.Reference,
            new(Limit: 101), CancellationToken.None);
        Require(badPage.Error == CompactFormError.InvalidArguments && backend.Mutations == 0, "page limit before opening");
        backend.ThrowDispatch = true;
        var lost = await runtime.OptionsAsync(form.Reference, form.ContextRevision, field.Reference, new(), CancellationToken.None);
        Require(lost.Error == CompactFormError.Unconfirmed && backend.Mutations == 1, "lost opening response without retry");
        backend = new FixtureBackend { NeedsOpen = true, ThrowRefreshAfterDispatch = true };
        runtime = new CompactFormRuntime(backend);
        form = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        field = form.Fields[0];
        var unreadable = await runtime.OptionsAsync(form.Reference, form.ContextRevision, field.Reference, new(), CancellationToken.None);
        Require(unreadable.Error == CompactFormError.Unconfirmed && backend.Mutations == 1, "lost opening postcondition without retry");
    }

    private static async Task FullValueEvidenceAsync()
    {
        string value = string.Concat(Enumerable.Repeat("🙂", 2001)) + "é";
        var backend = new FixtureBackend { TextValue = value };
        var runtime = new CompactFormRuntime(backend);
        CompactFormState form = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        var store = new CompactResultStore();
        var authority = new CompactResultAuthority("node", Guid.NewGuid());
        var responses = new CompactFormResponses(runtime, store, authority);
        var reply = responses.Read(form, ["Hidden"], new());
        var field = reply.Value!["fields"]![0]!;
        string clipped = field["value"]!.GetValue<string>();
        Require(CompactUnicode.Count(clipped) == 2000 && field["value_truncated"]!.GetValue<bool>(), "codepoint clipping");
        var reference = new CompactEvidenceReference(Guid.Parse(field["evidence_ref"]!.GetValue<string>()));
        var section = CompactResultSection.Parse(field["section"]!.GetValue<string>());
        Require(store.TryRead(authority, new(reference, section, 2000, 2), out var tail), "private full-value page");
        Require(tail.Value!.Text == "🙂é" && tail.Value.Complete && tail.Value.TotalCharacters == 2002, "exact full-value tail");
        Require(!store.TryRead(authority with
        {
            Session = Guid.NewGuid()
        }, new(reference, section, 0, 1), out _), "session authority");
        Require(form.Fields[1].Value == value, "clipping never mutates native verification state");
    }

    private static async Task CapacityAsync()
    {
        var backend = new FixtureBackend();
        var runtime = new CompactFormRuntime(backend);
        CompactFormState first = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        for (int index = 1; index <= CompactFormRuntime.MaximumForms; index++)
        {
            int processId = 10 + index;
            await runtime.ReadAsync(processId, Scope() with
            {
                ProcessId = processId
            }, null, CancellationToken.None);
        }
        Require(runtime.FormCount == CompactFormRuntime.MaximumForms, "bounded volatile forms");
        Require((await runtime.CurrentAsync(first.Reference, first.ContextRevision, CancellationToken.None)).Error
            == CompactFormError.UnknownOrExpiredForm, "oldest form evicted");
        Require(backend.Mutations == 0, "capacity checks never send input");
    }

    private static async Task ReferenceAuthorityAsync()
    {
        var backend = new FixtureBackend();
        var runtime = new CompactFormRuntime(backend);
        var foreign = await runtime.ReadAsync(11, Scope(), null, CancellationToken.None);
        Require(foreign.Error == CompactFormError.InvalidArguments && runtime.FormCount == 0, "scope process authority");
        CompactFormState form = (await runtime.ReadAsync(10, Scope(), null, CancellationToken.None)).Value!;
        var arguments = new System.Text.Json.Nodes.JsonObject
        {
            ["form_ref"] = form.Reference.Value.ToString(),
            ["context_rev"] = form.ContextRevision
        };
        Require(CompactFormCodec.TryRead("form.validate", arguments, out var parsed) && parsed.Value!.Form == form.Reference,
            "canonical opaque reference");
        arguments["form_ref"] = "{" + form.Reference.Value + "}";
        Require(CompactFormCodec.TryRead("form.validate", arguments, out parsed), "unknown reference is a lookup refusal");
        var unknown = await runtime.CurrentAsync(parsed.Value!.Form!, form.ContextRevision, CancellationToken.None);
        Require(unknown.Error == CompactFormError.UnknownOrExpiredForm && backend.Mutations == 0,
            "opaque references never accept alternate GUID spellings");
        backend.ThrowRefreshAfterDispatch = true;
        backend.ThrowDispatch = true;
        CompactFormField field = form.Fields[0];
        await runtime.SetAsync(form.Reference, form.ContextRevision, field.Reference,
            new(CompactFormValueKind.Option, OptionId: "Wide", OptionsRevision: field.OptionsRevision), CancellationToken.None);
        var unreadable = await runtime.CurrentAsync(form.Reference, form.ContextRevision, CancellationToken.None);
        Require(unreadable.Error == CompactFormError.ObservationUnavailable && backend.Mutations == 1,
            "unavailable observation is explicit and sends no extra input");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Compact form regression failed: " + name);
        }
    }

    // Controlled field snapshots retain native metadata, rather than using UI labels to invent constraints.
    private sealed class FixtureBackend : ICompactFormBackend
    {
        public int Mutations
        {
            get; private set;
        }
        public bool? Required { get; set; } = true;
        public bool ReadOnly
        {
            get; set;
        }
        public bool IgnoreWrites
        {
            get; set;
        }
        public bool ManyOptions
        {
            get; set;
        }
        public bool NeedsOpen
        {
            get; set;
        }
        public bool ThrowDispatch
        {
            get; set;
        }
        public bool ThrowRefreshAfterDispatch
        {
            get; set;
        }
        public string TextValue { get; set; } = "";
        public string Started { get; set; } = "start";
        private string _selected = "Basic";
        private int _revision;
        private bool _dispatched;

        public Task<CompactFormSnapshot> RefreshAsync(int processId, long windowHandle, CancellationToken cancellation)
        {
            if (_dispatched && ThrowRefreshAfterDispatch)
            {
                throw new IOException("Lost refresh response.");
            }
            var nodes = new CompactTreeNode[]
            {
                new(0, 1, CompactRole.Parse("Window"), "Door", "", "", true, false),
                new(2, 2, CompactRole.Parse("combo box"), "Door model", "model", _selected, true, false),
                new(2, 3, CompactRole.Parse("Edit"), "Hidden", "hidden", TextValue, true, false)
            };
            UiChoiceOption[] options = ManyOptions ? Enumerable.Range(0, 150).Select(index => new UiChoiceOption(
                "Option " + index, "Option " + index, true)).ToArray() : [new("Basic", "Basic", true), new("Wide", "Wide", true)];
            var choice = new UiChoiceMetadata(UiFormSupport.Available, "options-v1", options, true, NeedsOpen, _selected);
            var metadata = new Dictionary<int, UiFieldMetadata>
            {
                [2] = new(UiFormSupport.Available, UiFieldMetadataSource.ApplicationDeclaration, true, true, ReadOnly,
                    Required, _revision.ToString(), "model", null, choice),
                [3] = new(UiFormSupport.Available, UiFieldMetadataSource.ApplicationDeclaration, true, false, false,
                    false, _revision.ToString(), "hidden", [], UiChoiceMetadata.Unsupported)
            };

            var window = new CompactNativeWindow(10, "fixture", "1.0", "Door", 1, Started, CompactWindowKind.Main);
            return Task.FromResult(new CompactFormSnapshot(processId, "fresh", nodes, [window], metadata));
        }

        public Task<CompactFormDispatch> DispatchAsync(CompactFormMutation mutation, CancellationToken cancellation)
        {
            Require(mutation.SnapshotId == "fresh" && (mutation.OpenOnly || mutation.Context == _revision.ToString()),
                "fresh native snapshot/context");
            Mutations++;
            _dispatched = true;
            if (ThrowDispatch)
            {
                throw new IOException("Lost dispatch response.");
            }
            if (!IgnoreWrites && !mutation.OpenOnly)
            {
                _selected = mutation.Expected;
                _revision++;
            }

            return Task.FromResult(new CompactFormDispatch(true));
        }

        public void ExternalChange()
        {
            _revision++;
        }
    }

    // The test clock establishes expiry without relying on delays or a physical operator session.
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
        }
    }
}
