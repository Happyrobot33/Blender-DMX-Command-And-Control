using System.Globalization;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Server.TagHelpers;

[HtmlTargetElement("local-time", TagStructure = TagStructure.WithoutEndTag)]
public class LocalTimeTagHelper : TagHelper
{
    public DateTime? Value { get; set; }

    public bool Seconds { get; set; }

    public string Empty { get; set; } = "-";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagMode = TagMode.StartTagAndEndTag;
        if (Value is not DateTime value)
        {
            output.TagName = null;
            output.Content.SetContent(Empty);
            return;
        }

        // Timestamps are stored in UTC; values read back from MariaDB have Kind=Unspecified, and
        // emitting them without the "Z" would make browsers treat them as local time.
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        output.TagName = "time";
        output.Attributes.SetAttribute("datetime", utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        output.Attributes.SetAttribute("data-local-time", Seconds ? "seconds" : "minutes");
        output.Content.SetContent(utc.ToString(Seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
    }
}
