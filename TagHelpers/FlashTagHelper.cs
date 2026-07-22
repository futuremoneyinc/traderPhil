using Microsoft.AspNetCore.Razor.TagHelpers;

namespace TraderPhil.V4.Web.TagHelpers;

/// <summary>
/// Success / error / info banner shown after a POST. Renders nothing if
/// message is empty, so it's safe to leave on every page.
///
/// Usage:
///   <tp-flash kind="@Model.FlashKind" message="@Model.FlashMessage" />
///
/// kind can be: success | error | info | warning
/// message: any string; empty = nothing renders
/// </summary>
[HtmlTargetElement("tp-flash", TagStructure = TagStructure.WithoutEndTag)]
public class FlashTagHelper : TagHelper
{
    public string? Kind    { get; set; }
    public string? Message { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (string.IsNullOrEmpty(Message))
        {
            output.SuppressOutput();
            return;
        }

        var cls = Kind?.ToLowerInvariant() switch
        {
            "error"   => "tp-flash error",
            "warning" => "tp-flash warning",
            "info"    => "tp-flash info",
            _         => "tp-flash success"
        };

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", cls);
        output.Content.SetContent(Message);  // .SetContent encodes; safe for any text
    }
}
