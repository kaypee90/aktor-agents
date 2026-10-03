using System.Security;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace AgentRuntime.Infrastructure.Documents;

/// <summary>
/// Title-and-bullet slides to a 16:9 PowerPoint deck: an optional title slide, then one slide per
/// <see cref="SlideSpec"/>. Text is sized to the number of bullets, and a slide with too many
/// continues on the next. Parts are written as XML (validated against the Open XML schema in tests).
/// </summary>
internal static class PowerPointWriter
{
    private const string Ns = """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" """;
    private const int MaxBulletsPerSlide = 12;

    public static byte[] Write(string? title, List<SlideSpec> slides)
    {
        var pages = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) pages.Add(TitleSlide(title, null));
        foreach (var slide in slides)
        {
            var bullets = (slide.Bullets ?? []).Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
            var chunks = bullets.Chunk(MaxBulletsPerSlide).ToList();
            if (chunks.Count == 0) chunks.Add([]);
            for (var i = 0; i < chunks.Count; i++)
            {
                var heading = i == 0 ? slide.Title : $"{slide.Title} (continued)";
                pages.Add(ContentSlide(heading, chunks[i]));
            }
        }

        if (pages.Count == 0) pages.Add(TitleSlide(title ?? "Untitled", null));

        using var stream = new MemoryStream();
        using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentation = doc.AddPresentationPart();
            var master = presentation.AddNewPart<SlideMasterPart>("rIdMaster");
            var theme = master.AddNewPart<ThemePart>("rIdTheme");
            var layout = master.AddNewPart<SlideLayoutPart>("rIdLayout");
            layout.AddPart(master, "rIdMaster");
            presentation.AddPart(theme, "rIdTheme");
            Feed(theme, Theme);
            Feed(master, Master);
            Feed(layout, Layout);

            var ids = new StringBuilder();
            for (var i = 0; i < pages.Count; i++)
            {
                var slidePart = presentation.AddNewPart<SlidePart>($"rIdSlide{i + 1}");
                slidePart.AddPart(layout, "rIdLayout");
                Feed(slidePart, pages[i]);
                ids.Append($"""<p:sldId id="{256 + i}" r:id="rIdSlide{i + 1}"/>""");
            }

            Feed(presentation, $"""
                <p:presentation {Ns} saveSubsetFonts="1">
                  <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rIdMaster"/></p:sldMasterIdLst>
                  <p:sldIdLst>{ids}</p:sldIdLst>
                  <p:sldSz cx="12192000" cy="6858000"/>
                  <p:notesSz cx="6858000" cy="9144000"/>
                </p:presentation>
                """);
        }

        return stream.ToArray();
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" + "\n" + xml));
        part.FeedData(data);
    }

    private static string Esc(string text) =>
        SecurityElement.Escape(new string(text.Where(c => c is '\t' or '\n' or '\r' || !char.IsControl(c)).ToArray())) ?? string.Empty;

    private const string GroupHeader = """
        <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
        <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
        """;

    private static string TextBox(int id, string name, long x, long y, long cx, long cy, string anchor, string paragraphs) => $"""
        <p:sp>
          <p:nvSpPr><p:cNvPr id="{id}" name="{name}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
          <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
          <p:txBody><a:bodyPr wrap="square" anchor="{anchor}"><a:normAutofit/></a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody>
        </p:sp>
        """;

    private static string Run(string text, int size, bool bold = false, string color = "1F2937") =>
        $"""<a:r><a:rPr lang="en-US" sz="{size}"{(bold ? " b=\"1\"" : "")} dirty="0"><a:solidFill><a:srgbClr val="{color}"/></a:solidFill></a:rPr><a:t>{Esc(text)}</a:t></a:r>""";

    private static string TitleSlide(string title, string? subtitle) => Slide(
        TextBox(2, "Title", 838200, 2130425, 10515600, 1470025, "b", $"""<a:p><a:pPr algn="ctr"/>{Run(title, 4400, bold: true)}</a:p>""") +
        (string.IsNullOrEmpty(subtitle) ? string.Empty
            : TextBox(3, "Subtitle", 1524000, 3730625, 9144000, 1000000, "t", $"""<a:p><a:pPr algn="ctr"/>{Run(subtitle, 2400, color: "6B7280")}</a:p>""")));

    private static string ContentSlide(string? title, IReadOnlyList<string> bullets)
    {
        var size = bullets.Count <= 5 ? 2400 : bullets.Count <= 8 ? 2000 : 1600;
        var body = new StringBuilder();
        foreach (var bullet in bullets)
        {
            body.Append($"""<a:p><a:pPr marL="342900" indent="-342900"><a:spcBef><a:spcPts val="600"/></a:spcBef><a:buFont typeface="Arial"/><a:buChar char="•"/></a:pPr>{Run(bullet, size)}</a:p>""");
        }

        if (bullets.Count == 0) body.Append("<a:p><a:endParaRPr lang=\"en-US\" dirty=\"0\"/></a:p>");
        var titleBox = string.IsNullOrWhiteSpace(title) ? string.Empty
            : TextBox(2, "Title", 609600, 365125, 10972800, 1100000, "b", $"<a:p>{Run(title, 3600, bold: true)}</a:p>");
        return Slide(titleBox + TextBox(3, "Content", 609600, 1600200, 10972800, 4800600, "t", body.ToString()));
    }

    private static string Slide(string shapes) => $"""
        <p:sld {Ns}>
          <p:cSld><p:spTree>{GroupHeader}{shapes}</p:spTree></p:cSld>
          <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
        </p:sld>
        """;

    private static readonly string Layout = $"""
        <p:sldLayout {Ns} type="obj" preserve="1">
          <p:cSld name="Title and Content"><p:spTree>{GroupHeader}</p:spTree></p:cSld>
          <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
        </p:sldLayout>
        """;

    private static readonly string Master = $"""
        <p:sldMaster {Ns}>
          <p:cSld>
            <p:bg><p:bgRef idx="1001"><a:schemeClr val="bg1"/></p:bgRef></p:bg>
            <p:spTree>{GroupHeader}</p:spTree>
          </p:cSld>
          <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
          <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rIdLayout"/></p:sldLayoutIdLst>
          <p:txStyles>
            <p:titleStyle><a:lvl1pPr algn="l"><a:defRPr sz="3600" b="1"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mj-lt"/></a:defRPr></a:lvl1pPr></p:titleStyle>
            <p:bodyStyle><a:lvl1pPr marL="342900" indent="-342900"><a:buFont typeface="Arial"/><a:buChar char="•"/><a:defRPr sz="2400"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mn-lt"/></a:defRPr></a:lvl1pPr></p:bodyStyle>
            <p:otherStyle><a:lvl1pPr><a:defRPr sz="1800"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mn-lt"/></a:defRPr></a:lvl1pPr></p:otherStyle>
          </p:txStyles>
        </p:sldMaster>
        """;

    private const string Theme = """
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Aktor">
          <a:themeElements>
            <a:clrScheme name="Aktor">
              <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
              <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
              <a:dk2><a:srgbClr val="1F2937"/></a:dk2>
              <a:lt2><a:srgbClr val="F3F4F6"/></a:lt2>
              <a:accent1><a:srgbClr val="4F46E5"/></a:accent1>
              <a:accent2><a:srgbClr val="0EA5E9"/></a:accent2>
              <a:accent3><a:srgbClr val="10B981"/></a:accent3>
              <a:accent4><a:srgbClr val="F59E0B"/></a:accent4>
              <a:accent5><a:srgbClr val="EF4444"/></a:accent5>
              <a:accent6><a:srgbClr val="8B5CF6"/></a:accent6>
              <a:hlink><a:srgbClr val="2563EB"/></a:hlink>
              <a:folHlink><a:srgbClr val="7C3AED"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Aktor">
              <a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
              <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Aktor">
              <a:fillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              </a:fillStyleLst>
              <a:lnStyleLst>
                <a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                <a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                <a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
              </a:lnStyleLst>
              <a:effectStyleLst>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
                <a:effectStyle><a:effectLst/></a:effectStyle>
              </a:effectStyleLst>
              <a:bgFillStyleLst>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
              </a:bgFillStyleLst>
            </a:fmtScheme>
          </a:themeElements>
        </a:theme>
        """;
}
