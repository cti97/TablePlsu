using ICSharpCode.AvalonEdit;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.SqlEditor;

/// <summary>
/// Extension hooks into the SQL editor that other features fill in.
/// <para><b>Autocomplete (merge point):</b> the Completion feature sets <see cref="AttachCompletion"/> to
/// <c>TablePlsu.App.Features.Completion.SqlCompletion.Attach</c> (for example in its IFeature.Register, or the merger
/// replaces the single call site in <c>SqlEditorView.AttachCompletion</c> with a direct call).</para>
/// </summary>
public static class SqlEditorHooks
{
    /// <summary>Called once per editor view, after the editor is set up: <c>(editor, workspace)</c>.</summary>
    public static Action<TextEditor, IWorkspace>? AttachCompletion { get; set; }
}
