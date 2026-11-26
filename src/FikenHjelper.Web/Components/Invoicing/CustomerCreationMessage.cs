namespace FikenHjelper.Web.Components.Invoicing;

/// <summary>Result of creating a Fiken customer from a draft card.</summary>
public sealed record CustomerCreationMessage(string DraftKey, string Message, bool IsError);
