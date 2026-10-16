using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KLaniBot.Platforms;

// A cartridge selects one bounded, read-only portion of an application's native object model.
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationReadArea>))]
public enum ApplicationReadArea
{
    Status,
    Info,
    Api,
    Selection,
    Folders,
    Inbox,
    MailPage,
    Mail
}

// Mail sorting needs concise prose; original URL parameters remain available through an explicit Raw body read.
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationMailTextFormat>))]
public enum ApplicationMailTextFormat
{
    Raw,
    Compact,
    Readable
}

// Native application changes are explicit operations; arbitrary script execution is not an implicit UI permission.
[JsonConverter(typeof(JsonStringEnumConverter<ApplicationOperation>))]
public enum ApplicationOperation
{
    CreateFolder,
    CreateDraft,
    SetField,
    Save,
    Move,
    Delete,
    Display,
    Close
}

// The mail properties this adapter can set, with their native names retained at the COM boundary.
[JsonConverter(typeof(JsonStringEnumConverter<MailField>))]
public enum MailField
{
    Subject,
    Body,
    HTMLBody,
    To,
    CC,
    BCC,
    Importance,
    Sensitivity,
    ReadReceiptRequested,
    OriginatorDeliveryReportRequested
}

// The native type library reports whether a member reads a property, writes it or executes a method.
[JsonConverter(typeof(JsonStringEnumConverter<ApiMemberKind>))]
public enum ApiMemberKind
{
    Method,
    Get,
    Set
}

// One bounded query from MCP, converted from protocol fields before it reaches a platform backend.
public sealed record ApplicationObjectQuery(int ProcessId, ApplicationReadArea Area, string TypeName, string? FolderId,
    string? StoreId, int Offset, int Limit, int MaximumCharacters, string? EntryId = null, int BodyOffset = 0,
    ApplicationMailTextFormat TextFormat = ApplicationMailTextFormat.Raw);

// An operation binds a saved item and its expected source so stale observations cannot silently act in another folder.
public sealed record ApplicationObjectCommand(int ProcessId, ApplicationOperation Operation, string? EntryId, string? StoreId,
    string? ExpectedFolderId, string? DestinationFolderId, MailField? Field, JsonNode? Value, string Subject, string Body,
    string? ExpectedContentSha256 = null, string? ParentFolderId = null, string FolderName = "");

// Compact application identity is the first cartridge result, before a task loads any operation descriptions.
public sealed record ApplicationInformation(int ProcessId, string Variant, string Version, string Build, string Language,
    bool ObjectModelAvailable, string[] AccessRoutes);

// Program status comes from native processes in the operated Windows session, including a program without a visible window.
public sealed record ApplicationProgramStatus(bool Running, int[] ProcessIds);

// A member description is metadata from the installed library, not a claim that its execution has been accepted.
public sealed record ApiMember(string Name, ApiMemberKind Kind, int ParameterCount, string ReturnType, ApiParameter[] Parameters);

// A type-library parameter names its native type and whether it is input, output or optional.
public sealed record ApiParameter(string Name, string Type, bool Input, bool Output, bool Optional);

// Type and member discovery is paginated; the complete object model never needs to enter the model's context.
public sealed record ApplicationApiPage(string TypeName, int Total, int? NextOffset, string[] Types, ApiMember[] Members);

// A bounded mail read carries stable identifiers, its source and explicit truncation rather than an image of the editor.
public sealed record ApplicationMail(string EntryId, string StoreId, string FolderId, string Subject, string Sender,
    string Body, bool Truncated, int BodyOffset = 0, int? NextBodyOffset = null, int BodyLength = 0, string ContentSha256 = "",
    ApplicationMailTextFormat TextFormat = ApplicationMailTextFormat.Raw, string SenderAddress = "",
    long SourceTextUtf8Bytes = 0, long SourceHtmlUtf8Bytes = 0);

// A mailbox page contains inexpensive message headers; full body chunks are read only for selected item identities.
public sealed record ApplicationMailSummary(string EntryId, string StoreId, string FolderId, string Subject, string Sender,
    string Received, bool Unread, int AttachmentCount, string SenderAddress = "");

// Offsets are valid only while the collection stays unchanged; moves require a fresh first page of the source folder.
public sealed record ApplicationMailPage(int Total, int? NextOffset, ApplicationMailSummary[] Messages);

// A folder reference is enough to navigate another page or bind a move without position-dependent list selection.
public sealed record ApplicationFolder(string EntryId, string StoreId, string Name, bool HasChildren);

// The folder tree is loaded one level and one page at a time.
public sealed record ApplicationFolderPage(int Total, int? NextOffset, ApplicationFolder[] Folders);

// Mutation results are verified against the returned object; a caller can continue with its fresh item identity.
public sealed record ApplicationMutation(string EntryId, string StoreId, string FolderId, string Operation, bool Verified);

// Only the requested portion is serialized. Unsupported backends and native failures return a structured refusal.
public sealed record ApplicationObjectResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ApplicationProgramStatus? Status = null,
    [property: JsonPropertyName("info"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApplicationInformation? Info = null,
    [property: JsonPropertyName("api"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApplicationApiPage? Api = null,
    [property: JsonPropertyName("mail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApplicationMail? Mail = null,
    [property: JsonPropertyName("folders"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ApplicationFolderPage? Folders = null,
    [property: JsonPropertyName("folder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApplicationFolder? Folder = null,
    [property: JsonPropertyName("messages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ApplicationMailPage? Messages = null,
    [property: JsonPropertyName("mutation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ApplicationMutation? Mutation = null);
