using System.Globalization;
using Dentiva.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Dentiva.Core.Documents;

internal static class DocTheme
{
    public const string Primary = "#0E5FA6";
    public const string PrimaryDark = "#0A4478";
    public const string Ink = "#1F2933";
    public const string Muted = "#6B7A8C";
    public const string Line = "#DCE3EA";
    public const string Surface = "#F7F9FB";
    public const string Success = "#1E8E5A";
    public const string Danger = "#C0392B";

    /// <summary>
    /// QuestPDF exposes colours both as <c>Color</c> and as hex strings. Keeping our own string
    /// constant avoids ternaries that mix the two types (which the compiler cannot resolve).
    /// </summary>
    public const string White = "#FFFFFF";

    public const float BaseFont = 9.5f;

    public static PageSize Resolve(PrintLayout layout) => layout.Format switch
    {
        PaperFormat.A4 => PageSizes.A4,
        PaperFormat.A5 => PageSizes.A5,
        PaperFormat.Letter => PageSizes.Letter,
        PaperFormat.Thermal80mm => new PageSize(Mm(layout.ThermalWidthMm <= 0 ? 80 : layout.ThermalWidthMm), Mm(3000)),
        PaperFormat.Thermal58mm => new PageSize(Mm(58), Mm(3000)),
        _ => PageSizes.A4
    };

    public static bool IsThermal(PrintLayout layout) => layout.Format is PaperFormat.Thermal80mm or PaperFormat.Thermal58mm;

    /// <summary>Millimetres to PDF points.</summary>
    public static float Mm(float mm) => mm * 72f / 25.4f;

    public static string Money(decimal value, ClinicProfile clinic)
        => $"{clinic.CurrencySymbol}{value.ToString("N2", CultureInfo.InvariantCulture)}";

    public static string Date(DateTime? value) => value?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
    public static string DateTimeText(DateTime? value) => value?.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture) ?? "—";

    /// <summary>Applies semi-bold weight only when <paramref name="condition"/> holds.</summary>
    public static TextSpanDescriptor SemiBoldIf(this TextSpanDescriptor descriptor, bool condition)
        => condition ? descriptor.SemiBold() : descriptor;

    /// <summary>Loads a branding image, returning null when the file is missing or unreadable.</summary>
    public static byte[]? TryLoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Shared clinic letterhead used by all full-page documents.</summary>
internal static class DocHeader
{
    public static void Compose(IContainer container, ClinicProfile clinic, string documentTitle, string? reference, PrintLayout layout)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                var logo = layout.ShowLogo ? DocTheme.TryLoadImage(clinic.LogoPath) : null;
                if (logo is not null)
                {
                    row.ConstantItem(58).Height(58).AlignMiddle().Image(logo).FitArea();
                    row.ConstantItem(12);
                }

                row.RelativeItem().Column(info =>
                {
                    info.Item().Text(clinic.ClinicName).FontSize(16).SemiBold().FontColor(DocTheme.Primary);
                    if (!string.IsNullOrWhiteSpace(clinic.FullAddress))
                        info.Item().Text(clinic.FullAddress).FontSize(8.5f).FontColor(DocTheme.Muted);
                    if (!string.IsNullOrWhiteSpace(clinic.ContactLine))
                        info.Item().Text(clinic.ContactLine).FontSize(8.5f).FontColor(DocTheme.Muted);
                    if (!string.IsNullOrWhiteSpace(clinic.Registration))
                        info.Item().Text($"Reg: {clinic.Registration}").FontSize(8).FontColor(DocTheme.Muted);
                });

                row.ConstantItem(170).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(documentTitle.ToUpperInvariant())
                        .FontSize(15).Bold().FontColor(DocTheme.Ink).LetterSpacing(0.08f);
                    if (!string.IsNullOrWhiteSpace(reference))
                    {
                        right.Item().PaddingTop(2).AlignRight().Text(reference).FontSize(10).SemiBold().FontColor(DocTheme.Primary);
                    }
                });
            });

            column.Item().PaddingTop(10).LineHorizontal(1.4f).LineColor(DocTheme.Primary);
        });
    }

    public static void Footer(IContainer container, string? footerText)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(4).LineHorizontal(0.6f).LineColor(DocTheme.Line);
            col.Item().Row(row =>
            {
                row.RelativeItem().Text(footerText ?? string.Empty).FontSize(7.5f).FontColor(DocTheme.Muted);
                row.ConstantItem(120).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(DocTheme.Muted));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        });
    }

    public static void KeyValueBlock(IContainer container, string heading, IEnumerable<(string Label, string Value)> pairs)
    {
        container.Background(DocTheme.Surface).Padding(9).Column(col =>
        {
            col.Item().PaddingBottom(4).Text(heading.ToUpperInvariant())
                .FontSize(7.5f).SemiBold().FontColor(DocTheme.Muted).LetterSpacing(0.07f);

            foreach (var (label, value) in pairs)
            {
                col.Item().PaddingVertical(1.2f).Row(row =>
                {
                    row.ConstantItem(88).Text(label).FontSize(8.5f).FontColor(DocTheme.Muted);
                    row.RelativeItem().Text(string.IsNullOrWhiteSpace(value) ? "—" : value).FontSize(8.5f).FontColor(DocTheme.Ink);
                });
            }
        });
    }

    public static void SignatureArea(IContainer container, ClinicProfile clinic, string caption, PrintLayout layout)
    {
        container.AlignRight().Width(190).Column(col =>
        {
            var signature = layout.ShowSignature ? DocTheme.TryLoadImage(clinic.SignaturePath) : null;
            if (signature is not null)
            {
                col.Item().Height(42).AlignRight().AlignBottom().Image(signature).FitArea();
            }
            else
            {
                col.Item().Height(42);
            }

            col.Item().PaddingTop(2).LineHorizontal(0.8f).LineColor(DocTheme.Ink);
            col.Item().PaddingTop(3).AlignCenter().Text(caption).FontSize(8.5f).SemiBold().FontColor(DocTheme.Ink);
            if (!string.IsNullOrWhiteSpace(clinic.DentistRegistration))
            {
                col.Item().AlignCenter().Text($"Reg. {clinic.DentistRegistration}").FontSize(7.5f).FontColor(DocTheme.Muted);
            }
        });
    }
}

/// <summary>Professional A4/A5/thermal invoice.</summary>
public sealed class InvoiceDocument : IDocument
{
    private readonly InvoiceDocumentData _data;
    private readonly PrintLayout _layout;

    public InvoiceDocument(InvoiceDocumentData data, PrintLayout layout)
    {
        _data = data;
        _layout = layout;
    }

    public void Compose(IDocumentContainer container)
    {
        if (DocTheme.IsThermal(_layout))
        {
            ComposeThermal(container);
            return;
        }

        container.Page(page =>
        {
            page.Size(DocTheme.Resolve(_layout));
            page.Margin(DocTheme.Mm(_layout.MarginMm));
            page.DefaultTextStyle(s => s.FontSize(DocTheme.BaseFont).FontColor(DocTheme.Ink).FontFamily(Fonts.Calibri));

            page.Header().Element(c => DocHeader.Compose(c, _data.Clinic, "Invoice", _data.Invoice.InvoiceNumber, _layout));
            page.Content().PaddingVertical(12).Element(ComposeBody);
            page.Footer().Element(c => DocHeader.Footer(c, _data.Clinic.InvoiceFooter));
        });
    }

    private void ComposeBody(IContainer container)
    {
        var invoice = _data.Invoice;
        var clinic = _data.Clinic;

        container.Column(column =>
        {
            column.Spacing(12);

            column.Item().Row(row =>
            {
                row.RelativeItem().Element(c => DocHeader.KeyValueBlock(c, "Billed to", new[]
                {
                    ("Patient", _data.Patient?.FullName ?? invoice.PatientName),
                    ("Patient code", _data.Patient?.PatientCode ?? invoice.PatientCode),
                    ("Phone", _data.Patient?.Phone ?? "—"),
                    ("Address", _data.Patient?.Address ?? "—")
                }));

                row.ConstantItem(14);

                row.RelativeItem().Element(c => DocHeader.KeyValueBlock(c, "Invoice details", new[]
                {
                    ("Invoice no.", invoice.InvoiceNumber),
                    ("Invoice date", DocTheme.Date(invoice.InvoiceDate)),
                    ("Due date", invoice.DueDate is null ? "—" : DocTheme.Date(invoice.DueDate)),
                    ("Status", invoice.Status)
                }));
            });

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(26);
                    cd.RelativeColumn(4);
                    cd.ConstantColumn(46);
                    cd.ConstantColumn(72);
                    cd.ConstantColumn(62);
                    cd.ConstantColumn(78);
                });

                table.Header(header =>
                {
                    void Cell(string text, bool right = false)
                    {
                        var cell = header.Cell().Background(DocTheme.Primary).PaddingVertical(6).PaddingHorizontal(5);
                        var item = right ? cell.AlignRight() : cell;
                        item.Text(text).FontSize(8.5f).SemiBold().FontColor(DocTheme.White);
                    }

                    Cell("#");
                    Cell("Description");
                    Cell("Qty", true);
                    Cell("Unit price", true);
                    Cell("Discount", true);
                    Cell("Amount", true);
                });

                var index = 1;
                foreach (var item in invoice.Items)
                {
                    var background = index % 2 == 0 ? DocTheme.Surface : DocTheme.White;

                    table.Cell().Background(background).PaddingVertical(5).PaddingHorizontal(5)
                        .Text(index.ToString()).FontSize(8.5f).FontColor(DocTheme.Muted);

                    table.Cell().Background(background).PaddingVertical(5).PaddingHorizontal(5).Column(c =>
                    {
                        c.Item().Text(item.Description).FontSize(8.8f);
                        if (!string.IsNullOrWhiteSpace(item.ItemType))
                            c.Item().Text(item.ItemType).FontSize(7.5f).FontColor(DocTheme.Muted);
                    });

                    table.Cell().Background(background).PaddingVertical(5).PaddingHorizontal(5).AlignRight()
                        .Text(item.Quantity.ToString("0.##", CultureInfo.InvariantCulture)).FontSize(8.8f);
                    table.Cell().Background(background).PaddingVertical(5).PaddingHorizontal(5).AlignRight()
                        .Text(DocTheme.Money(item.UnitPrice, clinic)).FontSize(8.8f);
                    table.Cell().Background(background).PaddingVertical(5).PaddingHorizontal(5).AlignRight()
                        .Text(item.Discount > 0 ? DocTheme.Money(item.Discount, clinic) : "—").FontSize(8.8f);
                    table.Cell().Background(background).PaddingVertical(5).PaddingHorizontal(5).AlignRight()
                        .Text(DocTheme.Money(item.LineTotal, clinic)).FontSize(8.8f).SemiBold();

                    index++;
                }
            });

            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    if (!string.IsNullOrWhiteSpace(invoice.Notes))
                    {
                        left.Item().PaddingRight(16).Column(c =>
                        {
                            c.Item().Text("Notes").FontSize(7.5f).SemiBold().FontColor(DocTheme.Muted);
                            c.Item().PaddingTop(2).Text(invoice.Notes).FontSize(8.3f).FontColor(DocTheme.Ink);
                        });
                    }

                    if (_data.Payments.Count > 0)
                    {
                        left.Item().PaddingTop(10).PaddingRight(16).Column(c =>
                        {
                            c.Item().Text("Payments received").FontSize(7.5f).SemiBold().FontColor(DocTheme.Muted);
                            foreach (var payment in _data.Payments.OrderBy(p => p.PaymentDate))
                            {
                                c.Item().PaddingTop(2).Text(
                                    $"{DocTheme.Date(payment.PaymentDate)} — {DocTheme.Money(payment.Amount, clinic)} ({payment.Method})" +
                                    (payment.IsRefund ? " [refund]" : string.Empty))
                                    .FontSize(8f).FontColor(DocTheme.Ink);
                            }
                        });
                    }
                });

                row.ConstantItem(250).Column(totals =>
                {
                    void Line(string label, string value, bool emphasise = false, string? colour = null)
                    {
                        totals.Item().PaddingVertical(2.5f).Row(r =>
                        {
                            r.RelativeItem().Text(label)
                                .FontSize(emphasise ? 10f : 8.8f)
                                .SemiBoldIf(emphasise)
                                .FontColor(colour ?? (emphasise ? DocTheme.Ink : DocTheme.Muted));
                            r.ConstantItem(105).AlignRight().Text(value)
                                .FontSize(emphasise ? 10f : 8.8f)
                                .SemiBold()
                                .FontColor(colour ?? DocTheme.Ink);
                        });
                    }

                    Line("Subtotal", DocTheme.Money(invoice.Subtotal, clinic));

                    if (invoice.Discount > 0)
                    {
                        var label = invoice.DiscountType.Equals("Percent", StringComparison.OrdinalIgnoreCase)
                            ? $"Discount ({invoice.Discount:0.##}%)"
                            : "Discount";
                        var discountAmount = invoice.Subtotal - (invoice.Total - invoice.TaxAmount);
                        Line(label, "- " + DocTheme.Money(Math.Max(0, discountAmount), clinic));
                    }

                    if (clinic.TaxEnabled && invoice.TaxAmount > 0)
                    {
                        Line($"{clinic.TaxLabel} ({invoice.TaxRate:0.##}%)", DocTheme.Money(invoice.TaxAmount, clinic));
                    }

                    totals.Item().PaddingVertical(4).LineHorizontal(0.8f).LineColor(DocTheme.Line);
                    Line("Total", DocTheme.Money(invoice.Total, clinic), true);
                    Line("Paid", DocTheme.Money(invoice.PaidAmount, clinic), false, DocTheme.Success);

                    totals.Item().PaddingTop(4).Background(invoice.Due > 0 ? "#FDF1EF" : "#EDF7F1").Padding(7).Row(r =>
                    {
                        r.RelativeItem().Text("Amount due").FontSize(10).SemiBold()
                            .FontColor(invoice.Due > 0 ? DocTheme.Danger : DocTheme.Success);
                        r.ConstantItem(105).AlignRight().Text(DocTheme.Money(invoice.Due, clinic)).FontSize(11).Bold()
                            .FontColor(invoice.Due > 0 ? DocTheme.Danger : DocTheme.Success);
                    });
                });
            });

            column.Item().PaddingTop(22).Element(c =>
                DocHeader.SignatureArea(c, clinic, clinic.DentistName ?? "Authorised signature", _layout));
        });
    }

    private void ComposeThermal(IDocumentContainer container)
    {
        var invoice = _data.Invoice;
        var clinic = _data.Clinic;

        container.Page(page =>
        {
            page.Size(DocTheme.Resolve(_layout));
            page.Margin(DocTheme.Mm(4));
            page.DefaultTextStyle(s => s.FontSize(8f).FontColor(Colors.Black).FontFamily(Fonts.Calibri));

            page.Content().Column(col =>
            {
                col.Item().AlignCenter().Text(clinic.ClinicName).FontSize(11).Bold();
                if (!string.IsNullOrWhiteSpace(clinic.FullAddress))
                    col.Item().AlignCenter().Text(clinic.FullAddress).FontSize(7).LineHeight(1.1f);
                if (!string.IsNullOrWhiteSpace(clinic.Phone))
                    col.Item().AlignCenter().Text(clinic.Phone).FontSize(7);

                col.Item().PaddingVertical(4).LineHorizontal(0.7f);
                col.Item().AlignCenter().Text("INVOICE").FontSize(9).Bold();
                col.Item().PaddingBottom(3).AlignCenter().Text(invoice.InvoiceNumber).FontSize(8);

                col.Item().Text($"Date : {DocTheme.Date(invoice.InvoiceDate)}").FontSize(7.5f);
                col.Item().Text($"Patient : {_data.Patient?.FullName ?? invoice.PatientName}").FontSize(7.5f);
                col.Item().Text($"Code : {_data.Patient?.PatientCode ?? invoice.PatientCode}").FontSize(7.5f);

                col.Item().PaddingVertical(4).LineHorizontal(0.7f);

                foreach (var item in invoice.Items)
                {
                    col.Item().Text(item.Description).FontSize(7.8f);
                    col.Item().PaddingBottom(2).Row(r =>
                    {
                        r.RelativeItem().Text($"{item.Quantity:0.##} x {item.UnitPrice:N2}").FontSize(7.3f);
                        r.ConstantItem(62).AlignRight().Text(item.LineTotal.ToString("N2", CultureInfo.InvariantCulture)).FontSize(7.8f);
                    });
                }

                col.Item().PaddingVertical(3).LineHorizontal(0.7f);

                void Line(string label, decimal value, bool bold = false)
                {
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Text(label).FontSize(bold ? 8.5f : 7.8f).SemiBoldIf(bold);
                        r.ConstantItem(62).AlignRight().Text(value.ToString("N2", CultureInfo.InvariantCulture))
                            .FontSize(bold ? 8.5f : 7.8f).SemiBoldIf(bold);
                    });
                }

                Line("Subtotal", invoice.Subtotal);
                if (clinic.TaxEnabled && invoice.TaxAmount > 0) Line(clinic.TaxLabel, invoice.TaxAmount);
                Line("Total", invoice.Total, true);
                Line("Paid", invoice.PaidAmount);
                Line("Due", invoice.Due, true);

                col.Item().PaddingVertical(4).LineHorizontal(0.7f);
                col.Item().AlignCenter().Text(clinic.ReceiptFooter ?? "Thank you for visiting.").FontSize(7).LineHeight(1.2f);
                col.Item().PaddingTop(2).AlignCenter().Text(DocTheme.DateTimeText(DateTime.Now)).FontSize(6.5f);
            });
        });
    }
}

/// <summary>Money receipt, supporting both page and thermal roll output.</summary>
public sealed class ReceiptDocument : IDocument
{
    private readonly ReceiptDocumentData _data;
    private readonly PrintLayout _layout;

    public ReceiptDocument(ReceiptDocumentData data, PrintLayout layout)
    {
        _data = data;
        _layout = layout;
    }

    public void Compose(IDocumentContainer container)
    {
        if (DocTheme.IsThermal(_layout))
        {
            ComposeThermal(container);
            return;
        }

        var payment = _data.Payment;
        var clinic = _data.Clinic;

        container.Page(page =>
        {
            page.Size(DocTheme.Resolve(_layout));
            page.Margin(DocTheme.Mm(_layout.MarginMm));
            page.DefaultTextStyle(s => s.FontSize(DocTheme.BaseFont).FontColor(DocTheme.Ink).FontFamily(Fonts.Calibri));

            page.Header().Element(c => DocHeader.Compose(c, clinic,
                payment.IsRefund ? "Refund Receipt" : "Money Receipt", payment.ReceiptNumber, _layout));

            page.Content().PaddingVertical(14).Column(column =>
            {
                column.Spacing(14);

                column.Item().Row(row =>
                {
                    row.RelativeItem().Element(c => DocHeader.KeyValueBlock(c, "Received from", new[]
                    {
                        ("Patient", _data.Patient?.FullName ?? payment.PatientName),
                        ("Patient code", _data.Patient?.PatientCode ?? payment.PatientCode),
                        ("Phone", _data.Patient?.Phone ?? "—")
                    }));

                    row.ConstantItem(14);

                    row.RelativeItem().Element(c => DocHeader.KeyValueBlock(c, "Payment details", new[]
                    {
                        ("Receipt no.", payment.ReceiptNumber),
                        ("Date", DocTheme.DateTimeText(payment.PaymentDate)),
                        ("Method", payment.Method),
                        ("Reference", string.IsNullOrWhiteSpace(payment.Reference) ? "—" : payment.Reference!),
                        ("Invoice", payment.InvoiceNumber ?? _data.Invoice?.InvoiceNumber ?? "—")
                    }));
                });

                column.Item().Background(DocTheme.Primary).Padding(14).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(payment.IsRefund ? "Amount refunded" : "Amount received")
                            .FontSize(9).FontColor("#C8DCEF");
                        c.Item().PaddingTop(3).Text(DocTheme.Money(payment.Amount, clinic))
                            .FontSize(21).Bold().FontColor(DocTheme.White);
                    });

                    row.ConstantItem(180).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text("Remaining due").FontSize(9).FontColor("#C8DCEF");
                        c.Item().PaddingTop(3).AlignRight().Text(DocTheme.Money(_data.RemainingDue, clinic))
                            .FontSize(15).SemiBold().FontColor(DocTheme.White);
                    });
                });

                if (!string.IsNullOrWhiteSpace(payment.Notes))
                {
                    column.Item().Column(c =>
                    {
                        c.Item().Text("Notes").FontSize(7.5f).SemiBold().FontColor(DocTheme.Muted);
                        c.Item().PaddingTop(2).Text(payment.Notes).FontSize(8.5f);
                    });
                }

                column.Item().PaddingTop(20).Row(row =>
                {
                    row.RelativeItem().AlignBottom().Text($"Received by: {payment.ReceivedBy ?? "—"}")
                        .FontSize(8.5f).FontColor(DocTheme.Muted);
                    row.ConstantItem(200).Element(c =>
                        DocHeader.SignatureArea(c, clinic, clinic.DentistName ?? "Authorised signature", _layout));
                });
            });

            page.Footer().Element(c => DocHeader.Footer(c, clinic.ReceiptFooter));
        });
    }

    private void ComposeThermal(IDocumentContainer container)
    {
        var payment = _data.Payment;
        var clinic = _data.Clinic;

        container.Page(page =>
        {
            page.Size(DocTheme.Resolve(_layout));
            page.Margin(DocTheme.Mm(4));
            page.DefaultTextStyle(s => s.FontSize(8f).FontColor(Colors.Black).FontFamily(Fonts.Calibri));

            page.Content().Column(col =>
            {
                col.Item().AlignCenter().Text(clinic.ClinicName).FontSize(11).Bold();
                if (!string.IsNullOrWhiteSpace(clinic.Phone))
                    col.Item().AlignCenter().Text(clinic.Phone).FontSize(7);

                col.Item().PaddingVertical(4).LineHorizontal(0.7f);
                col.Item().AlignCenter().Text(payment.IsRefund ? "REFUND RECEIPT" : "MONEY RECEIPT").FontSize(9).Bold();
                col.Item().PaddingBottom(3).AlignCenter().Text(payment.ReceiptNumber).FontSize(8);

                col.Item().Text($"Date : {DocTheme.DateTimeText(payment.PaymentDate)}").FontSize(7.5f);
                col.Item().Text($"Patient : {_data.Patient?.FullName ?? payment.PatientName}").FontSize(7.5f);
                col.Item().Text($"Code : {_data.Patient?.PatientCode ?? payment.PatientCode}").FontSize(7.5f);
                col.Item().Text($"Method : {payment.Method}").FontSize(7.5f);
                if (!string.IsNullOrWhiteSpace(payment.Reference))
                    col.Item().Text($"Ref : {payment.Reference}").FontSize(7.5f);

                col.Item().PaddingVertical(4).LineHorizontal(0.7f);

                col.Item().Row(r =>
                {
                    r.RelativeItem().Text("Amount").FontSize(9).Bold();
                    r.ConstantItem(70).AlignRight().Text(payment.Amount.ToString("N2", CultureInfo.InvariantCulture)).FontSize(9).Bold();
                });
                col.Item().Row(r =>
                {
                    r.RelativeItem().Text("Due").FontSize(8);
                    r.ConstantItem(70).AlignRight().Text(_data.RemainingDue.ToString("N2", CultureInfo.InvariantCulture)).FontSize(8);
                });

                col.Item().PaddingVertical(4).LineHorizontal(0.7f);
                col.Item().AlignCenter().Text(clinic.ReceiptFooter ?? "Thank you.").FontSize(7).LineHeight(1.2f);
            });
        });
    }
}

/// <summary>Clinic-branded prescription with the standard Rx layout.</summary>
public sealed class PrescriptionDocument : IDocument
{
    private readonly PrescriptionDocumentData _data;
    private readonly PrintLayout _layout;

    public PrescriptionDocument(PrescriptionDocumentData data, PrintLayout layout)
    {
        _data = data;
        _layout = layout;
    }

    public void Compose(IDocumentContainer container)
    {
        var rx = _data.Prescription;
        var clinic = _data.Clinic;
        var patient = _data.Patient;

        container.Page(page =>
        {
            page.Size(DocTheme.Resolve(_layout) == PageSizes.A5 ? PageSizes.A5 : PageSizes.A4);
            page.Margin(DocTheme.Mm(_layout.MarginMm));
            page.DefaultTextStyle(s => s.FontSize(DocTheme.BaseFont).FontColor(DocTheme.Ink).FontFamily(Fonts.Calibri));

            page.Header().Column(header =>
            {
                header.Item().Row(row =>
                {
                    var logo = _layout.ShowLogo ? DocTheme.TryLoadImage(clinic.LogoPath) : null;
                    if (logo is not null)
                    {
                        row.ConstantItem(56).Height(56).AlignMiddle().Image(logo).FitArea();
                        row.ConstantItem(12);
                    }

                    row.RelativeItem().Column(info =>
                    {
                        info.Item().Text(clinic.ClinicName).FontSize(15).SemiBold().FontColor(DocTheme.Primary);
                        if (!string.IsNullOrWhiteSpace(clinic.FullAddress))
                            info.Item().Text(clinic.FullAddress).FontSize(8).FontColor(DocTheme.Muted);
                        if (!string.IsNullOrWhiteSpace(clinic.ContactLine))
                            info.Item().Text(clinic.ContactLine).FontSize(8).FontColor(DocTheme.Muted);
                    });

                    row.ConstantItem(190).AlignRight().Column(right =>
                    {
                        if (!string.IsNullOrWhiteSpace(clinic.DentistName))
                            right.Item().AlignRight().Text(clinic.DentistName).FontSize(11).SemiBold();
                        if (!string.IsNullOrWhiteSpace(clinic.DentistQualification))
                            right.Item().AlignRight().Text(clinic.DentistQualification).FontSize(8).FontColor(DocTheme.Muted);
                        if (!string.IsNullOrWhiteSpace(clinic.DentistSpecialty))
                            right.Item().AlignRight().Text(clinic.DentistSpecialty).FontSize(8).FontColor(DocTheme.Muted);
                        if (!string.IsNullOrWhiteSpace(clinic.DentistRegistration))
                            right.Item().AlignRight().Text($"Reg. {clinic.DentistRegistration}").FontSize(8).FontColor(DocTheme.Muted);
                    });
                });

                header.Item().PaddingTop(8).LineHorizontal(1.4f).LineColor(DocTheme.Primary);

                header.Item().PaddingTop(8).Row(row =>
                {
                    row.RelativeItem().Text($"Patient: {patient?.FullName ?? "—"}").FontSize(9).SemiBold();
                    row.ConstantItem(96).Text($"Code: {patient?.PatientCode ?? "—"}").FontSize(8.5f).FontColor(DocTheme.Muted);
                    row.ConstantItem(64).Text(patient?.Age is { } age ? $"Age: {age}" : "Age: —").FontSize(8.5f).FontColor(DocTheme.Muted);
                    row.ConstantItem(74).Text($"Sex: {patient?.Gender ?? "—"}").FontSize(8.5f).FontColor(DocTheme.Muted);
                    row.ConstantItem(104).AlignRight().Text(DocTheme.Date(rx.IssuedDate)).FontSize(8.5f).FontColor(DocTheme.Muted);
                });

                header.Item().PaddingTop(6).LineHorizontal(0.6f).LineColor(DocTheme.Line);
            });

            page.Content().PaddingVertical(10).Row(row =>
            {
                // Left clinical column
                row.ConstantItem(168).PaddingRight(10).Column(left =>
                {
                    void Block(string title, string? body)
                    {
                        if (string.IsNullOrWhiteSpace(body)) return;
                        left.Item().PaddingBottom(9).Column(c =>
                        {
                            c.Item().Text(title.ToUpperInvariant()).FontSize(7.5f).SemiBold().FontColor(DocTheme.Muted).LetterSpacing(0.06f);
                            c.Item().PaddingTop(2).Text(body).FontSize(8.3f).LineHeight(1.25f);
                        });
                    }

                    Block("Diagnosis", rx.Diagnosis);
                    Block("Allergies", patient?.Allergies);
                    Block("Medical conditions", patient?.MedicalConditions);
                    Block("Current medications", patient?.CurrentMedications);
                    Block("Advice", rx.Advice);
                });

                row.ConstantItem(1).LineVertical(0.7f).LineColor(DocTheme.Line);

                // Rx column
                row.RelativeItem().PaddingLeft(12).Column(right =>
                {
                    right.Item().PaddingBottom(6).Text("℞").FontSize(26).Bold().FontColor(DocTheme.Primary);

                    var index = 1;
                    foreach (var item in rx.Items)
                    {
                        right.Item().PaddingBottom(9).Row(line =>
                        {
                            line.ConstantItem(20).Text($"{index}.").FontSize(9).SemiBold().FontColor(DocTheme.Muted);
                            line.RelativeItem().Column(c =>
                            {
                                var title = item.MedicineName;
                                if (!string.IsNullOrWhiteSpace(item.Strength)) title += $" {item.Strength}";
                                c.Item().Text(title).FontSize(10).SemiBold();

                                if (!string.IsNullOrWhiteSpace(item.GenericName))
                                    c.Item().Text(item.GenericName).FontSize(7.8f).Italic().FontColor(DocTheme.Muted);

                                var schedule = new List<string>();
                                if (!string.IsNullOrWhiteSpace(item.Dosage)) schedule.Add(item.Dosage!);
                                if (!string.IsNullOrWhiteSpace(item.Frequency)) schedule.Add(item.Frequency!);
                                if (!string.IsNullOrWhiteSpace(item.Duration)) schedule.Add(item.Duration!);
                                if (!string.IsNullOrWhiteSpace(item.Route)) schedule.Add(item.Route!);
                                if (!string.IsNullOrWhiteSpace(item.MealRelation)) schedule.Add(item.MealRelation!);
                                if (schedule.Count > 0)
                                    c.Item().PaddingTop(1).Text(string.Join("  •  ", schedule)).FontSize(8.4f);

                                if (!string.IsNullOrWhiteSpace(item.Quantity))
                                    c.Item().Text($"Quantity: {item.Quantity}").FontSize(7.8f).FontColor(DocTheme.Muted);
                                if (!string.IsNullOrWhiteSpace(item.Instructions))
                                    c.Item().Text(item.Instructions).FontSize(7.8f).Italic().FontColor(DocTheme.Muted);
                            });
                        });

                        index++;
                    }

                    if (rx.FollowUpDate is { } follow)
                    {
                        right.Item().PaddingTop(8).Background(DocTheme.Surface).Padding(7)
                            .Text($"Next visit: {DocTheme.Date(follow)}").FontSize(9).SemiBold().FontColor(DocTheme.Primary);
                    }

                    if (!string.IsNullOrWhiteSpace(rx.Notes))
                    {
                        right.Item().PaddingTop(8).Text(rx.Notes).FontSize(8.2f).FontColor(DocTheme.Muted);
                    }

                    right.Item().PaddingTop(26).Element(c =>
                        DocHeader.SignatureArea(c, clinic, clinic.DentistName ?? "Signature", _layout));
                });
            });

            page.Footer().Element(c => DocHeader.Footer(c,
                $"Prescription {rx.PrescriptionNumber}  •  Generated by Dentiva"));
        });
    }
}
