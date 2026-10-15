using System.Windows;
using System.Windows.Controls;

namespace WindowsAICompanion.Desktop;

/// <summary>
/// Routes chat messages to the correct DataTemplate based on role:
///   - "user"      → UserMessageTemplate  (plain TextBlock, right-aligned bubble)
///   - "assistant" → AssistantMessageTemplate  (MdXaml MarkdownScrollViewer)
/// </summary>
internal sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    /// <summary>Template applied to user messages.</summary>
    public DataTemplate? UserTemplate { get; set; }

    /// <summary>Template applied to assistant (AI) messages — renders Markdown.</summary>
    public DataTemplate? AssistantTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is ChatMessage message)
        {
            return message.Role == "user" ? UserTemplate : AssistantTemplate;
        }

        return base.SelectTemplate(item, container);
    }
}
