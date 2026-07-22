using Microsoft.AspNetCore.Razor.TagHelpers;

namespace TraderPhil.V4.Web.TagHelpers;

/// <summary>
/// Active/inactive pill with a colored dot. Used in card headers to signal
/// whether a feature is on, off, or in a problem state.
///
/// Usage:
///   <tp-status-pill state="active" label="Active" />
///   <tp-status-pill state="danger" label="Engaged" />
///   <tp-status-pill state="inactive" label="Not configured" />
///   <tp-status-pill state="warning" label="Needs attention" />
///
/// Renders as:
///   <span class="tp-ladder-pill {state-class}">
///     <span class="tp-ladder-pill-dot"></span>
///     {label}
///   </span>
///
/// Replaces the @{ var pillCls = active ? "tp-ladder-pill active" : ... } block
/// pattern that kept triggering Razor RZ1010 errors.
/// </summary>
[HtmlTargetElement("tp-status-pill", TagStructure = TagStructure.WithoutEndTag)]
public class StatusPillTagHelper : TagHelper
{
    /// <summary>One of: active | inactive | danger | warning</summary>
    public string State { get; set; } = "inactive";

    /// <summary>Text shown next to the dot.</summary>
    public string Label { get; set; } = "";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var stateClass = State?.ToLowerInvariant() switch
        {
            "active"   => "tp-ladder-pill active",
            "danger"   => "tp-ladder-pill active tp-pill-danger",
            "warning"  => "tp-ladder-pill tp-pill-warning",
            _          => "tp-ladder-pill inactive"
        };

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", stateClass);
        output.Content.SetHtmlContent(
            "<span class=\"tp-ladder-pill-dot\"></span>" +
            System.Net.WebUtility.HtmlEncode(Label ?? ""));
    }
}
