using Microsoft.AspNetCore.Razor.TagHelpers;

namespace TraderPhil.V4.Web.TagHelpers;

/// <summary>
/// Icon + label + value tile, like the 4 tiles in the Profit Ladder card
/// (Total Targets, Available, Consumed, Range).
///
/// Usage:
///   <tp-stat-tile color="green" label="Total Targets" value="1,495">
///     <svg ...>...</svg>
///   </tp-stat-tile>
///
///   <tp-stat-tile color="blue" label="Range" value="$1.00 to $500.00" value-size="small">
///     <svg ...>...</svg>
///   </tp-stat-tile>
///
/// Renders as:
///   <div class="tp-ladder-tile">
///     <div class="tp-ladder-tile-icon tp-ladder-tile-icon-{color}">{svg}</div>
///     <div>
///       <div class="tp-ladder-tile-label">{label}</div>
///       <div class="tp-ladder-tile-value {sm?}">{value}</div>
///     </div>
///   </div>
/// </summary>
[HtmlTargetElement("tp-stat-tile")]
public class StatTileTagHelper : TagHelper
{
    /// <summary>One of: green | yellow | blue | red.</summary>
    public string Color { get; set; } = "green";

    /// <summary>Caption above the value, like "Total Targets".</summary>
    public string Label { get; set; } = "";

    /// <summary>The number/text to display large.</summary>
    public string Value { get; set; } = "";

    /// <summary>"small" to use the smaller font for long string values like ranges.</summary>
    public string ValueSize { get; set; } = "";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var iconColorClass = Color?.ToLowerInvariant() switch
        {
            "green"   => "tp-ladder-tile-icon-targets",  // also reused by Available
            "yellow"  => "tp-ladder-tile-icon-consumed",
            "blue"    => "tp-ladder-tile-icon-range",
            "red"     => "tp-ladder-tile-icon-estop",
            _         => "tp-ladder-tile-icon-targets"
        };

        var valueClass = ValueSize?.ToLowerInvariant() == "small"
            ? "tp-ladder-tile-value tp-ladder-tile-value-sm"
            : "tp-ladder-tile-value";

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "tp-ladder-tile");

        var iconContent = await output.GetChildContentAsync();
        var iconSvg = iconContent.GetContent();  // raw HTML from the child SVG

        var labelHtml = System.Net.WebUtility.HtmlEncode(Label ?? "");
        var valueHtml = System.Net.WebUtility.HtmlEncode(Value ?? "");

        output.Content.SetHtmlContent(
            $"<div class=\"tp-ladder-tile-icon {iconColorClass}\">{iconSvg}</div>" +
            $"<div>" +
            $"<div class=\"tp-ladder-tile-label\">{labelHtml}</div>" +
            $"<div class=\"{valueClass}\">{valueHtml}</div>" +
            $"</div>");
    }
}
