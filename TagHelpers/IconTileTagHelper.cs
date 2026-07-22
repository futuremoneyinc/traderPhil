using Microsoft.AspNetCore.Razor.TagHelpers;

namespace TraderPhil.V4.Web.TagHelpers;

/// <summary>
/// Colored rounded square that holds an SVG icon. Used in card headers.
///
/// Usage:
///   <tp-icon-tile color="green">
///     <svg ...>...</svg>
///   </tp-icon-tile>
///
///   <tp-icon-tile color="blue" size="small">
///     <svg ...>...</svg>
///   </tp-icon-tile>
///
/// Color tints are bound to the existing CSS classes:
///   green    - tp-card-icon-symbicore   (accumulation / growth / available)
///   yellow   - tp-card-icon-woolchipper (defensive / consumed / warning)
///   blue     - tp-card-icon-treasury    (treasury / range / accent)
///   red      - tp-card-icon-estop       (danger / e-stop)
///   neutral  - default surface tile, no special tint
///
/// Renders as:
///   <div class="tp-card-icon {color-class}" aria-hidden="true">
///     {child SVG content}
///   </div>
/// </summary>
[HtmlTargetElement("tp-icon-tile")]
public class IconTileTagHelper : TagHelper
{
    /// <summary>One of: green | yellow | blue | red | neutral (default).</summary>
    public string Color { get; set; } = "neutral";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var colorClass = Color?.ToLowerInvariant() switch
        {
            "green"   => "tp-card-icon-symbicore",
            "yellow"  => "tp-card-icon-woolchipper",
            "blue"    => "tp-card-icon-treasury",
            "red"     => "tp-card-icon-estop",
            _         => ""
        };

        var fullClass = string.IsNullOrEmpty(colorClass)
            ? "tp-card-icon"
            : "tp-card-icon " + colorClass;

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", fullClass);
        output.Attributes.SetAttribute("aria-hidden", "true");

        // Preserve the SVG (or whatever else) the caller put inside.
        var child = await output.GetChildContentAsync();
        output.Content.SetHtmlContent(child);
    }
}
