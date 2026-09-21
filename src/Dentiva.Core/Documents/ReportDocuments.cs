using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Dentiva.Core.Documents;

/// <summary>
/// Generic branded tabular report. Long tables paginate automatically and the header repeats,
/// so content is never clipped regardless of row count.
/// </summary>
public sealed class TabularReportDocument : IDocument
{
    private readonly TabularReportData _data;

    public TabularReportDocument(TabularReportData data)
    {
        _data = data;
    }

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(_data.Landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(DocTheme.Mm(12));
            page.DefaultTextStyle(s => s.FontSize(8.6f).FontColor(DocTheme.Ink).FontFamily(Fonts.Calibri));

            page.Header().Column(header =>
            {
                header.Item().Row(row =>
                {
                    var logo = DocTheme.TryLoadImage(_data.Clinic.LogoPath);
                    if (logo is not null)
                    {
                        row.ConstantItem(42).Height(42).AlignMiddle().Image(logo).FitArea();
                        row.ConstantItem(10);
                    }

                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(_data.Clinic.ClinicName).FontSize(13).SemiBold().FontColor(DocTheme.Primary);
                        if (!string.IsNullOrWhiteSpace(_data.Clinic.FullAddress))
                            c.Item().Text(_data.Clinic.FullAddress).FontSize(7.8f).FontColor(DocTheme.Muted);
                    });

                    row.ConstantItem(230).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(_data.Title).FontSize(13).Bold();
                        var range = BuildRange();
                        if (!string.IsNullOrWhiteSpace(range))
                            c.Item().AlignRight().Text(range).FontSize(8).FontColor(DocTheme.Muted);
                        if (!string.IsNullOrWhiteSpace(_data.Subtitle))
                            c.Item().AlignRight().Text(_data.Subtitle).FontSize(8).FontColor(DocTheme.Muted);
                    });
                });

                header.Item().PaddingTop(8).LineHorizontal(1.2f).LineColor(DocTheme.Primary);
            });

            page.Content().PaddingVertical(10).Column(column =>
            {
                if (_data.Rows.Count == 0)
                {
                    column.Item().PaddingTop(40).AlignCenter().Text("No records match the selected criteria.")
                        .FontSize(10).FontColor(DocTheme.Muted);
                }
                else
                {
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            for (var i = 0; i < _data.Columns.Count; i++)
                            {
                                var width = i < _data.ColumnWidths.Count ? _data.ColumnWidths[i] : 1f;
                                cd.RelativeColumn(width);
                            }
                        });

                        table.Header(header =>
                        {
                            foreach (var col in _data.Columns)
                            {
                                header.Cell().Background(DocTheme.Primary).PaddingVertical(5).PaddingHorizontal(4)
                                    .Text(col).FontSize(8).SemiBold().FontColor(Colors.White);
                            }
                        });

                        var index = 0;
                        foreach (var row in _data.Rows)
                        {
                            var background = index % 2 == 0 ? Colors.White : DocTheme.Surface;
                            for (var i = 0; i < _data.Columns.Count; i++)
                            {
                                var value = i < row.Length ? row[i] : string.Empty;
                                table.Cell().Background(background).PaddingVertical(4).PaddingHorizontal(4)
                                    .Text(value).FontSize(8.2f);
                            }

                            index++;
                        }
                    });
                }

                if (_data.Totals.Count > 0)
                {
                    column.Item().PaddingTop(12).AlignRight().Width(280).Column(totals =>
                    {
                        foreach (var (label, value) in _data.Totals)
                        {
                            totals.Item().PaddingVertical(2).Row(r =>
                            {
                                r.RelativeItem().Text(label).FontSize(9).FontColor(DocTheme.Muted);
                                r.ConstantItem(130).AlignRight().Text(value).FontSize(9).SemiBold();
                            });
                        }
                    });
                }

                column.Item().PaddingTop(10).Text($"{_data.Rows.Count} record(s)").FontSize(7.8f).FontColor(DocTheme.Muted);
            });

            page.Footer().Element(c => DocHeader.Footer(c,
                $"Generated {DateTime.Now:dd MMM yyyy, hh:mm tt} by Dentiva"));
        });
    }

    private string BuildRange()
    {
        if (_data.From is null && _data.To is null) return string.Empty;
        var from = _data.From?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "start";
        var to = _data.To?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "today";
        return $"{from} — {to}";
    }
}

/// <summary>Complete clinical summary for a single patient.</summary>
public sealed class PatientSummaryDocument : IDocument
{
    private readonly PatientSummaryData _data;

    public PatientSummaryDocument(PatientSummaryData data)
    {
        _data = data;
    }

    public void Compose(IDocumentContainer container)
    {
        var p = _data.Patient;
        var clinic = _data.Clinic;

        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(DocTheme.Mm(12));
            page.DefaultTextStyle(s => s.FontSize(8.8f).FontColor(DocTheme.Ink).FontFamily(Fonts.Calibri));

            page.Header().Element(c => DocHeader.Compose(c, clinic, "Patient Summary", p.PatientCode, PrintLayout.Default));

            page.Content().PaddingVertical(12).Column(column =>
            {
                column.Spacing(12);

                column.Item().Row(row =>
                {
                    row.RelativeItem().Element(c => DocHeader.KeyValueBlock(c, "Patient", new[]
                    {
                        ("Name", p.FullName),
                        ("Code", p.PatientCode),
                        ("Age / Sex", $"{(p.Age?.ToString() ?? "—")} / {p.Gender ?? "—"}"),
                        ("Date of birth", DocTheme.Date(p.DateOfBirth)),
                        ("Blood group", p.BloodGroup ?? "—")
                    }));

                    row.ConstantItem(12);

                    row.RelativeItem().Element(c => DocHeader.KeyValueBlock(c, "Contact", new[]
                    {
                        ("Phone", p.Phone ?? "—"),
                        ("Alternate", p.AlternatePhone ?? "—"),
                        ("Email", p.Email ?? "—"),
                        ("Address", p.Address ?? "—"),
                        ("Emergency", $"{p.EmergencyContactName ?? "—"} {(string.IsNullOrWhiteSpace(p.EmergencyPhone) ? string.Empty : "(" + p.EmergencyPhone + ")")}")
                    }));

                    row.ConstantItem(12);

                    row.RelativeItem().Element(c => DocHeader.KeyValueBlock(c, "Account", new[]
                    {
                        ("Total billed", DocTheme.Money(_data.TotalBilled, clinic)),
                        ("Total paid", DocTheme.Money(_data.TotalPaid, clinic)),
                        ("Outstanding", DocTheme.Money(_data.Outstanding, clinic)),
                        ("Visits", _data.Visits.Count.ToString()),
                        ("Treatments", _data.Treatments.Count.ToString())
                    }));
                });

                // Medical alerts
                var alerts = new List<(string, string)>();
                if (!string.IsNullOrWhiteSpace(p.Allergies)) alerts.Add(("Allergies", p.Allergies!));
                if (!string.IsNullOrWhiteSpace(p.MedicalConditions)) alerts.Add(("Conditions", p.MedicalConditions!));
                if (!string.IsNullOrWhiteSpace(p.CurrentMedications)) alerts.Add(("Medications", p.CurrentMedications!));
                if (!string.IsNullOrWhiteSpace(p.DentalHistory)) alerts.Add(("Dental history", p.DentalHistory!));
                if (!string.IsNullOrWhiteSpace(p.MedicalHistory)) alerts.Add(("Medical history", p.MedicalHistory!));
                if (!string.IsNullOrWhiteSpace(p.SurgicalHistory)) alerts.Add(("Surgical history", p.SurgicalHistory!));

                if (alerts.Count > 0)
                {
                    column.Item().Element(c => DocHeader.KeyValueBlock(c, "Clinical information", alerts));
                }

                if (_data.Visits.Count > 0)
                {
                    column.Item().Element(c => Section(c, "Visit history"));
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(62);
                            cd.RelativeColumn(2);
                            cd.RelativeColumn(3);
                            cd.RelativeColumn(2);
                            cd.ConstantColumn(62);
                        });

                        HeaderRow(table, "Date", "Reason", "Diagnosis / notes", "Treatment", "Follow-up");

                        var i = 0;
                        foreach (var v in _data.Visits.OrderByDescending(x => x.VisitDate))
                        {
                            var bg = i++ % 2 == 0 ? Colors.White : DocTheme.Surface;
                            Cell(table, bg, DocTheme.Date(v.VisitDate));
                            Cell(table, bg, v.Reason ?? v.Complaint ?? "—");
                            Cell(table, bg, Combine(v.Diagnosis, v.ClinicalNotes));
                            Cell(table, bg, v.TreatmentPerformed ?? "—");
                            Cell(table, bg, DocTheme.Date(v.FollowUpDate));
                        }
                    });
                }

                if (_data.Treatments.Count > 0)
                {
                    column.Item().Element(c => Section(c, "Treatment history"));
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(62);
                            cd.RelativeColumn(2);
                            cd.RelativeColumn(2);
                            cd.ConstantColumn(62);
                            cd.ConstantColumn(58);
                            cd.ConstantColumn(62);
                        });

                        HeaderRow(table, "Date", "Category", "Procedure", "Tooth", "Status", "Cost");

                        var i = 0;
                        foreach (var t in _data.Treatments.OrderByDescending(x => x.TreatmentDate))
                        {
                            var bg = i++ % 2 == 0 ? Colors.White : DocTheme.Surface;
                            Cell(table, bg, DocTheme.Date(t.TreatmentDate));
                            Cell(table, bg, t.CategoryName ?? "—");
                            Cell(table, bg, t.ProcedureName);
                            Cell(table, bg, t.Teeth ?? "—");
                            Cell(table, bg, t.Status);
                            Cell(table, bg, DocTheme.Money(t.NetCost, clinic));
                        }
                    });
                }

                if (_data.Prescriptions.Count > 0)
                {
                    column.Item().Element(c => Section(c, "Prescriptions"));
                    foreach (var rx in _data.Prescriptions.OrderByDescending(x => x.IssuedDate).Take(20))
                    {
                        column.Item().PaddingBottom(4).Column(c =>
                        {
                            c.Item().Text($"{DocTheme.Date(rx.IssuedDate)}  •  {rx.PrescriptionNumber}")
                                .FontSize(8.6f).SemiBold();
                            foreach (var item in rx.Items)
                            {
                                var parts = new List<string> { item.MedicineName };
                                if (!string.IsNullOrWhiteSpace(item.Strength)) parts.Add(item.Strength!);
                                if (!string.IsNullOrWhiteSpace(item.Dosage)) parts.Add(item.Dosage!);
                                if (!string.IsNullOrWhiteSpace(item.Duration)) parts.Add(item.Duration!);
                                c.Item().Text("   • " + string.Join(" — ", parts)).FontSize(8.2f).FontColor(DocTheme.Muted);
                            }
                        });
                    }
                }
            });

            page.Footer().Element(c => DocHeader.Footer(c,
                $"Confidential patient record  •  {clinic.ClinicName}  •  Generated {DateTime.Now:dd MMM yyyy}"));
        });
    }

    private static string Combine(params string?[] values)
    {
        var parts = values.Where(v => !string.IsNullOrWhiteSpace(v));
        var joined = string.Join(" — ", parts);
        return string.IsNullOrWhiteSpace(joined) ? "—" : joined;
    }

    private static void Section(IContainer container, string title)
    {
        container.PaddingTop(4).Column(c =>
        {
            c.Item().Text(title.ToUpperInvariant()).FontSize(8.5f).SemiBold().FontColor(DocTheme.Primary).LetterSpacing(0.07f);
            c.Item().PaddingTop(3).LineHorizontal(0.7f).LineColor(DocTheme.Line);
        });
    }

    private static void HeaderRow(TableDescriptor table, params string[] headers)
    {
        table.Header(header =>
        {
            foreach (var h in headers)
            {
                header.Cell().Background(DocTheme.Primary).PaddingVertical(4).PaddingHorizontal(4)
                    .Text(h).FontSize(7.8f).SemiBold().FontColor(Colors.White);
            }
        });
    }

    private static void Cell(TableDescriptor table, string background, string value)
    {
        table.Cell().Background(background).PaddingVertical(3.5f).PaddingHorizontal(4)
            .Text(value).FontSize(8f);
    }
}
