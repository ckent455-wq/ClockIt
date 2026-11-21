using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

// Generates a self-contained, printable landscape PDF without requiring an installed PDF printer.
internal static class PayrollPdf
{
    private const int Width = 1650, Height = 1275, Margin = 80;
    private static readonly int[] ColumnWidths = { 390, 390, 220, 220, 220 };
    private static readonly string[] Headers = { "Clock in (local)", "Clock out (local)", "Unpaid break", "Time worked", "Decimal hours" };

    public static byte[] Create(DateTime firstDay, int openShifts, IList<string[]> rows)
    {
        var pages = new List<byte[]>();
        int rowIndex = 0;
        using (var titleFont = new Font("Arial", 32, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var textFont = new Font("Arial", 21, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var boldFont = new Font("Arial", 21, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var labelFont = new Font("Arial", 19, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var headingBrush = new SolidBrush(Color.FromArgb(31, 58, 77)))
        using (var mutedBrush = new SolidBrush(Color.FromArgb(68, 84, 96)))
        using (var sectionBrush = new SolidBrush(Color.FromArgb(229, 237, 242)))
        using (var totalBrush = new SolidBrush(Color.FromArgb(240, 245, 247)))
        {
            do
            {
                using (var bitmap = new Bitmap(Width, Height))
                using (var g = Graphics.FromImage(bitmap))
                using (var image = new MemoryStream())
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString("Weekly payroll timesheet", titleFont, headingBrush, Margin, 42);
                    string period = firstDay.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) + " – " + firstDay.AddDays(6).ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
                    g.DrawString("Pay period: " + period, textFont, Brushes.Black, Margin, 90);
                    string notice = openShifts == 0 ? "Completed shifts only." :
                        "ATTENTION: " + openShifts.ToString(CultureInfo.InvariantCulture) + (openShifts == 1 ? " open shift is" : " open shifts are") + " excluded from this report.";
                    g.DrawString(notice, labelFont, openShifts == 0 ? mutedBrush : Brushes.Firebrick, Margin, 126);
                    int y = 170;
                    string currentEmployee = null;
                    while (rowIndex < rows.Count)
                    {
                        string[] row = rows[rowIndex];
                        bool total = row[0] == "Employee total";
                        bool newSection = currentEmployee != row[1];
                        int headerHeight = 0;
                        if (newSection)
                        {
                            int nameHeight = (int)Math.Ceiling(g.MeasureString(row[1] ?? "", boldFont, 1100).Height);
                            headerHeight = Math.Max(46, nameHeight + 16) + 39;
                        }
                        string[] values = total
                            ? new[] { "Weekly total", "", row[4], row[5], row[6] }
                            : new[] { FriendlyTime(row[2]), FriendlyTime(row[3]), row[4], row[5], row[6] };
                        Font font = total ? boldFont : textFont;
                        int height = 43;
                        for (int i = 0; i < values.Length; i++)
                            height = Math.Max(height, (int)Math.Ceiling(g.MeasureString(values[i] ?? "", font, ColumnWidths[i] - 14).Height) + 14);
                        if (y + headerHeight + height > Height - Margin - 35 && y > 170) break;
                        if (y + headerHeight + height > Height - Margin - 35)
                            throw new InvalidOperationException("A report row is too long to fit on a page.");
                        if (newSection)
                        {
                            int sectionHeight = headerHeight - 39;
                            g.FillRectangle(sectionBrush, Margin, y, Width - 2 * Margin, sectionHeight);
                            g.DrawString(row[1] ?? "", boldFont, headingBrush, new RectangleF(Margin + 10, y + 8, 1100, sectionHeight - 12));
                            using (var align = new StringFormat { Alignment = StringAlignment.Far })
                                g.DrawString(row[7], labelFont, mutedBrush, new RectangleF(Width - Margin - 200, y + 10, 190, sectionHeight - 12), align);
                            y += sectionHeight;
                            int headingX = Margin;
                            for (int i = 0; i < Headers.Length; i++)
                            {
                                g.DrawString(Headers[i], labelFont, mutedBrush, headingX + 7, y + 9);
                                headingX += ColumnWidths[i];
                            }
                            y += 39;
                            currentEmployee = row[1];
                        }
                        if (total) g.FillRectangle(totalBrush, Margin, y, Width - 2 * Margin, height);
                        int x = Margin;
                        for (int i = 0; i < values.Length; i++)
                        {
                            g.DrawString(values[i] ?? "", font, Brushes.Black, new RectangleF(x + 7, y + 7, ColumnWidths[i] - 14, height - 14));
                            x += ColumnWidths[i];
                        }
                        g.DrawLine(Pens.LightGray, Margin, y + height, Width - Margin, y + height);
                        y += height + (total ? 18 : 0);
                        rowIndex++;
                        if (total) currentEmployee = null;
                    }
                    g.DrawString("Times shown in local time  •  Durations are hours:minutes:seconds", labelFont, mutedBrush, Margin, Height - 54);
                    using (var align = new StringFormat { Alignment = StringAlignment.Far })
                        g.DrawString("Page " + (pages.Count + 1), labelFont, mutedBrush, new RectangleF(Width - Margin - 150, Height - 54, 150, 30), align);
                    bitmap.Save(image, ImageFormat.Jpeg);
                    pages.Add(image.ToArray());
                }
            } while (rowIndex < rows.Count);
        }
        return BuildPdf(pages);
    }

    private static string FriendlyTime(string value)
    {
        DateTime date;
        return DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date)
            ? date.ToString("ddd, MMM d  h:mm:ss tt", CultureInfo.InvariantCulture) : value;
    }

    private static byte[] BuildPdf(List<byte[]> pages)
    {
        using (var output = new MemoryStream())
        {
            var offsets = new List<long> { 0 };
            Action<string> write = value => { byte[] bytes = Encoding.ASCII.GetBytes(value); output.Write(bytes, 0, bytes.Length); };
            Action<int, string> obj = (number, body) => { offsets.Add(output.Position); write(number + " 0 obj\n" + body + "\nendobj\n"); };
            write("%PDF-1.4\n%ClockIt\n");
            obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
            var kids = new StringBuilder();
            for (int i = 0; i < pages.Count; i++) kids.Append(3 + i * 3).Append(" 0 R ");
            obj(2, "<< /Type /Pages /Count " + pages.Count + " /Kids [ " + kids + "] >>");
            for (int i = 0; i < pages.Count; i++)
            {
                int page = 3 + i * 3, image = page + 1, content = page + 2;
                obj(page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 792 612] /Resources << /XObject << /Im0 " + image + " 0 R >> >> /Contents " + content + " 0 R >>");
                offsets.Add(output.Position);
                write(image + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + Width + " /Height " + Height + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + pages[i].Length + " >>\nstream\n");
                output.Write(pages[i], 0, pages[i].Length);
                write("\nendstream\nendobj\n");
                string drawing = "q\n792 0 0 612 0 0 cm\n/Im0 Do\nQ\n";
                obj(content, "<< /Length " + Encoding.ASCII.GetByteCount(drawing) + " >>\nstream\n" + drawing + "endstream");
            }
            long xref = output.Position;
            write("xref\n0 " + offsets.Count + "\n0000000000 65535 f \n");
            for (int i = 1; i < offsets.Count; i++) write(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            write("trailer\n<< /Size " + offsets.Count + " /Root 1 0 R >>\nstartxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
            return output.ToArray();
        }
    }
}

