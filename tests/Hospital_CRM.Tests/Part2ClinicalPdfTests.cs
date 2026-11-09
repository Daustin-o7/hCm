using System.Text;
using System.Text.RegularExpressions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using UglyToad.PdfPig;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2ClinicalPdfTests
{
    [Fact]
    public void Prescription_LongContentAndAllMedicinesHaveContinuationPagesAndEmbeddedFonts()
    {
        var consultation = new Consultation { Version = 3, Doctor = new User { Name = "Synthetic doctor" },
            Appointment = new Appointment { Patient = new Patient { Name = "Synthetic patient", Uhid = "SYNTHETIC-ONLY" } },
            VerificationQrCode = "RX-123456789ABC", DigitalSignatureHash = "v2:" + new string('A', 64),
            FollowUpNotes = "LAST FOLLOWUP MARKER", AdvisedInvestigations = "INVESTIGATION MARKER" };
        var prescription = new Prescription();
        for (var i = 1; i <= 24; i++)
            prescription.Items.Add(new PrescriptionItem { GenericName = $"MEDICINE_{i:D2}", DosageForm = "Tablet", Strength = "50mg", Route = "Oral",
                Frequency = "Twice daily", Duration = 14, DurationUnit = "Days", TotalQuantity = 28, Timing = "After food",
                SpecialInstructions = string.Join(" ", Enumerable.Repeat("complete instruction", 65)) + $" END_{i:D2}" });
        var bytes = new PdfService().GeneratePrescriptionPdf(prescription, consultation, new Clinic { Name = "SYNTHETIC CLINIC" }, true);
        var text = PdfTestReader.ReadText(bytes);
        using var document = PdfDocument.Open(bytes, new ParsingOptions { UseActualText = true });
        var pages = document.NumberOfPages;
        Assert.True(pages > 3);
        Assert.Contains("/FontFile2", Encoding.Latin1.GetString(bytes));
        Assert.Equal(pages, Regex.Matches(text, "DUPLICATE COPY").Count);
        Assert.Equal(pages, Regex.Matches(text, "Verify code:").Count);
        Assert.Matches(@"Medicine \d+ - continued", text);
        for (var i = 1; i <= 24; i++) { Assert.Contains($"MEDICINE_{i:D2}", text); Assert.Contains($"END_{i:D2}", text); }
        Assert.Contains("LAST FOLLOWUP MARKER", text);
        Assert.Contains("INVESTIGATION MARKER", text);
        Assert.Contains("Registration: Not recorded", text);
        Assert.DoesNotContain("MBBS", text);
        foreach (var page in document.GetPages())
        {
            Assert.Contains($"Page {page.Number}", page.Text);
            Assert.Contains("Version 3", page.Text);
            Assert.Contains("Clinical content seal (SHA-256, v2)", page.Text);
            Assert.Contains("not a certificate-backed digital signature", page.Text);
            foreach (var letter in page.Letters.Where(letter => letter.Value != "DUPLICATE COPY"))
            {
                Assert.InRange(letter.BoundingBox.Left, 35, 555);
                Assert.InRange(letter.BoundingBox.Right, 35, 560);
                Assert.InRange(letter.BoundingBox.Bottom, 25, 820);
                Assert.InRange(letter.BoundingBox.Top, 25, 820);
            }
        }
        // Optional synthetic artifact for independent PDF-reader checks; never clinical data.
        var artifact = Environment.GetEnvironmentVariable("PART2_CLINICAL_PDF_ARTIFACT");
        if (!string.IsNullOrWhiteSpace(artifact))
        {
            File.WriteAllBytes(artifact, bytes);
            File.WriteAllText(Path.ChangeExtension(artifact, ".txt"), text);
        }
    }

    [Fact]
    public void SharedCanvas_InvoiceAndCreditNoteRemainValid_AndWinAnsiTextIsPreserved()
    {
        var service = new PdfService();
        var clinic = new Clinic { Name = "Synthetic café — clinic" };
        var invoice = Part1SaleReadinessTests.Bill(Guid.NewGuid(), 1, 100);
        invoice.WalkInCustomerName = "Synthetic patient";
        var credit = new CreditNote { CreditNoteNumber = "CN-SYNTHETIC", Amount = 10, Reason = "Synthetic adjustment", Invoice = invoice };
        foreach (var bytes in new[] { service.GenerateInvoicePdf(invoice, clinic, false), service.GenerateCreditNotePdf(credit, invoice, clinic) })
        {
            var text = Encoding.Latin1.GetString(bytes);
            AssertPdfOffsets(text);
            Assert.Single(Regex.Matches(text, @"/Type /Page\b"));
            Assert.Contains((byte)0xe9, bytes);
            Assert.Contains((byte)0x97, bytes);
            Assert.Contains("100.00", text);
        }
    }

    [Theory]
    [InlineData("தமிழ்")]
    [InlineData("తెలుగు")]
    [InlineData("ಕನ್ನಡ")]
    [InlineData("മലയാളം")]
    [InlineData("বাংলা")]
    [InlineData("ગુજરાતી")]
    [InlineData("ਪੰਜਾਬੀ")]
    [InlineData("ଓଡ଼ିଆ")]
    [InlineData("中文")]
    [InlineData("العربية")]
    [InlineData("اردو: کھانے کے بعد دوا لیں")]
    [InlineData("Ελληνικά β")]
    [InlineData("Кириллица")]
    [InlineData("עברית")]
    [InlineData("😀")]
    [InlineData("\u007f")]
    [InlineData("\u0081")]
    [InlineData("\u200e")]
    [InlineData("\u202e")]
    public void Prescription_UnsupportedScriptOrControlFailsInsteadOfCorruptingInstructions(string instructions)
    {
        var rx = new Prescription();
        rx.Items.Add(new PrescriptionItem { GenericName = "Synthetic medicine", SpecialInstructions = instructions });
        Assert.Throws<EncoderFallbackException>(() => new PdfService().GeneratePrescriptionPdf(rx, new Consultation(), null, false));
    }

    [Theory]
    [InlineData("Café: 5µg — ₹20; e\u0301; Łukasz, José, François, Đặng")]
    [InlineData("हिन्दी — भोजन के बाद एक गोली लें।")]
    [InlineData("क्ष त्र ज्ञ श्र क्र प्र स्त्र — कि की कृ र्क क्षि; दवा 500mg")]
    [InlineData("हिन्दी क्\u200dष क्\u200cष")]
    [InlineData("First line\r\nSecond line\t5mg\nThird line")]
    public void Prescription_EnglishAndHindiPreserveOriginalText(string instructions)
    {
        var rx = new Prescription();
        rx.Items.Add(new PrescriptionItem { GenericName = "Synthetic medicine", SpecialInstructions = instructions });
        var bytes = new PdfService().GeneratePrescriptionPdf(rx, new Consultation(), null, false);
        var text = PdfTestReader.ReadText(bytes);
        Assert.Contains(Regex.Replace(instructions, @"\s", ""), Regex.Replace(text, @"\s", ""));
        Assert.DoesNotContain("\0", text, StringComparison.Ordinal);
        Assert.Contains("Legacy seal - medicine and note content not verified", text);
    }

    [Theory]
    [InlineData("क्ष", 1)]
    [InlineData("त्र", 1)]
    [InlineData("ज्ञ", 1)]
    [InlineData("क्षि", 2)]
    public void Prescription_HindiConjunctsUseShapedGlyphs(string diagnosis, int expectedGlyphs)
    {
        var bytes = new PdfService().GeneratePrescriptionPdf(new Prescription(), new Consultation { Diagnosis = diagnosis }, null, false);
        // Read physical glyphs without ActualText: unshaped consonant/virama sequences must not pass.
        using var document = PdfDocument.Open(bytes);
        var glyphs = document.GetPage(1).Letters.Where(letter => letter.FontName?.Contains("Devanagari", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(expectedGlyphs, glyphs.Length);
        if (diagnosis == "क्षि") Assert.True(glyphs[0].BoundingBox.Left < glyphs[1].BoundingBox.Left);
        Assert.Contains(diagnosis, PdfTestReader.ReadText(bytes));
    }

    [Fact]
    public void Prescription_MalformedUtf16FailsInsteadOfReplacingClinicalText()
    {
        var rx = new Prescription();
        rx.Items.Add(new PrescriptionItem { GenericName = "Synthetic medicine", SpecialInstructions = new string('\ud800', 1) });
        Assert.Throws<EncoderFallbackException>(() => new PdfService().GeneratePrescriptionPdf(rx, new Consultation(), null, false));
    }

    [Fact]
    public void Prescription_EnglishHindiFieldsAndLongUnbrokenTextSurvivePagination()
    {
        var instructions = string.Concat(Enumerable.Repeat("हिन्दीऔषधि", 500)) + " END DEVANAGARI\n"
            + string.Join(" ", Enumerable.Repeat("क्ष त्र ज्ञ श्र कि र्क क्षि — भोजन के बाद 500mg", 100)) + " END HINDI";
        var consultation = new Consultation { Doctor = new User { Name = "चिकित्सक" }, DoctorQualifications = "एमबीबीएस",
            DoctorRegistrationNumber = "REG-123", Diagnosis = "सिरदर्द", FollowUpNotes = "पुनः जाँच",
            Appointment = new Appointment { Patient = new Patient { Name = "रोगी — José", Address = "रोगी का पता", Uhid = "SYNTHETIC" } },
            Version = 2, VerificationQrCode = "RX-SYNTHETIC", DigitalSignatureHash = "v2:" + new string('A', 64) };
        var rx = new Prescription();
        rx.Items.Add(new PrescriptionItem { GenericName = "Synthetic medicine", SpecialInstructions = instructions });
        var bytes = new PdfService().GeneratePrescriptionPdf(rx, consultation, new Clinic { Name = "क्लिनिक", Address = "क्लिनिक का पता" }, true);
        var text = PdfTestReader.ReadText(bytes);
        foreach (var expected in new[] { "चिकित्सक", "एमबीबीएस", "सिरदर्द", "पुनः जाँच", "रोगी — José", "रोगी का पता", "क्लिनिक", "क्लिनिक का पता", "END DEVANAGARI", "END HINDI" })
            Assert.Contains(expected, text);
        Assert.Equal(500, Regex.Matches(Regex.Replace(text, @"\s", ""), "हिन्दीऔषधि").Count);
        Assert.Equal(100, Regex.Matches(Regex.Replace(text, @"\s", ""), "क्षत्रज्ञश्रकिर्कक्षि—भोजनकेबाद500mg").Count);
        using var document = PdfDocument.Open(bytes, new ParsingOptions { UseActualText = true });
        foreach (var page in document.GetPages())
            foreach (var letter in page.Letters.Where(letter => letter.Value != "DUPLICATE COPY"))
            {
                Assert.InRange(letter.BoundingBox.Left, 35, 555);
                Assert.InRange(letter.BoundingBox.Right, 35, 560);
                Assert.InRange(letter.BoundingBox.Bottom, 25, 820);
                Assert.InRange(letter.BoundingBox.Top, 25, 820);
            }
        var artifact = Environment.GetEnvironmentVariable("PART3_ENGLISH_HINDI_PDF_ARTIFACT");
        if (!string.IsNullOrWhiteSpace(artifact))
        {
            File.WriteAllBytes(artifact, bytes);
            File.WriteAllText(Path.ChangeExtension(artifact, ".txt"), text);
        }
    }

    [Fact]
    public async Task Prescription_ConcurrentRendersKeepPatientTextIsolated()
    {
        var service = new PdfService();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            var marker = $"SYNTHETIC-PATIENT-{index:D2}";
            var consultation = new Consultation { Diagnosis = marker, FollowUpNotes = "हिन्दी क्षि 500mg" };
            var bytes = service.GeneratePrescriptionPdf(new Prescription(), consultation, null, false);
            var text = PdfTestReader.ReadText(bytes);
            Assert.Contains(marker, text);
            Assert.Single(Regex.Matches(text, "SYNTHETIC-PATIENT-"));
            Assert.Contains("हिन्दी", text);
            Assert.Contains("क्षि", text);
            Assert.Contains("500mg", text);
        })));
    }

    [Fact]
    public void Prescription_ProfessionalEnglishSample_GeneratesCleanArtifact()
    {
        var clinic = new Clinic
        {
            Name = "Apollo Clinic & Healthcare Centre",
            LegalName = "Apollo Clinic & Healthcare Centre",
            Address = "Plot 104, Golf Course Road, Sector 43, Gurugram, Haryana - 122002",
            Phone = "+91 124 4567890",
            Email = "care@apolloclinic.com"
        };
        var consultation = new Consultation
        {
            Doctor = new User { Name = "Dr. Ananya Sen" },
            DoctorQualifications = "MBBS, MD (Internal Medicine) - AIIMS New Delhi",
            DoctorRegistrationNumber = "MCI-2015-74829",
            Diagnosis = "Acute Bronchitis with Bronchospasm & Low-Grade Pyrexia",
            FollowUpDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            FollowUpNotes = "Review with CBC report after 5 days, or earlier if breathing difficulty develops. Stay well hydrated with warm fluids.",
            AdvisedInvestigations = "Complete Blood Count (CBC), Chest X-Ray (PA View) if fever persists beyond 3 days",
            Appointment = new Appointment
            {
                Patient = new Patient
                {
                    Name = "Rohan Malhotra",
                    Uhid = "UHID-DEL-2026-08492",
                    ApproxAge = 34,
                    Gender = Gender.Male,
                    Phone = "+91 98101 23456",
                    Address = "B-4/122 Safdarjung Enclave, New Delhi - 110029"
                }
            },
            Version = 1,
            VerificationQrCode = "RX-APOLLO-2026-00482",
            DigitalSignatureHash = "v2:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            SignedAt = DateTime.UtcNow
        };
        var rx = new Prescription();
        rx.Items.Add(new PrescriptionItem
        {
            BrandName = "Augmentin 625 Duo",
            GenericName = "Amoxicillin (500mg) + Clavulanic Acid (125mg)",
            DosageForm = "Tablet",
            Strength = "625mg",
            Route = "Oral",
            Frequency = "1-0-1 (Twice Daily)",
            Duration = 5,
            DurationUnit = "Days",
            TotalQuantity = 10,
            Timing = "After food",
            SpecialInstructions = "Take with meals. Complete the entire 5-day course without skipping doses."
        });
        rx.Items.Add(new PrescriptionItem
        {
            BrandName = "Dolo 650",
            GenericName = "Paracetamol",
            DosageForm = "Tablet",
            Strength = "650mg",
            Route = "Oral",
            Frequency = "SOS (As needed, max 3 times daily)",
            Duration = 3,
            DurationUnit = "Days",
            TotalQuantity = 6,
            Timing = "After food",
            SpecialInstructions = "Take only in case of fever above 100°F or body ache. Maintain minimum 6 hours gap."
        });
        rx.Items.Add(new PrescriptionItem
        {
            BrandName = "Ascoril LS",
            GenericName = "Levosalbutamol + Ambroxol + Guaiphenesin",
            DosageForm = "Syrup",
            Strength = "100ml",
            Route = "Oral",
            Frequency = "1-1-1 (Thrice Daily, 10ml)",
            Duration = 5,
            DurationUnit = "Days",
            TotalQuantity = 1,
            Timing = "After food",
            SpecialInstructions = "Shake well before use. May cause mild tremors; avoid taking immediately before driving."
        });
        rx.Items.Add(new PrescriptionItem
        {
            BrandName = "Pantocid 40",
            GenericName = "Pantoprazole",
            DosageForm = "Tablet",
            Strength = "40mg",
            Route = "Oral",
            Frequency = "1-0-0 (Once Daily)",
            Duration = 5,
            DurationUnit = "Days",
            TotalQuantity = 5,
            Timing = "Before food",
            SpecialInstructions = "Take in the morning on an empty stomach, 30 minutes before breakfast."
        });

        var bytes = new PdfService().GeneratePrescriptionPdf(rx, consultation, clinic, false);
        var text = PdfTestReader.ReadText(bytes);

        Assert.Contains("ELECTRONIC PRESCRIPTION", text);
        Assert.Contains("Apollo Clinic", text);
        Assert.Contains("Dr. Ananya Sen", text);
        Assert.Contains("Rohan Malhotra", text);
        Assert.Contains("Augmentin 625 Duo", text);
        Assert.Contains("Dolo 650", text);
        Assert.Contains("Ascoril LS", text);
        Assert.Contains("Pantocid 40", text);

        // Save pristine English sample to artifacts directory
        var rootDir = FindSolutionRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();
        var targetDir = Path.Combine(rootDir, "artifacts", "samples");
        Directory.CreateDirectory(targetDir);
        File.WriteAllBytes(Path.Combine(targetDir, "prescription-english-sample.pdf"), bytes);
        File.WriteAllBytes(Path.Combine(targetDir, "prescription-bilingual-sample.pdf"), bytes);
        File.WriteAllText(Path.Combine(targetDir, "prescription-english-sample.txt"), text);
        File.WriteAllText(Path.Combine(targetDir, "prescription-bilingual-sample.txt"), text);
    }

    private static string? FindSolutionRoot(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Hospital_CRM.sln")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static void AssertPdfOffsets(string text)
    {
        Assert.StartsWith("%PDF-1.4", text);
        var start = Regex.Match(text, @"startxref\n(\d+)\n");
        Assert.True(start.Success);
        var xref = int.Parse(start.Groups[1].Value);
        Assert.StartsWith("xref\n", text[xref..]);
        var entries = Regex.Matches(text[xref..], @"(?m)^(\d{10}) 00000 n ");
        for (var i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj\n", text[int.Parse(entries[i].Groups[1].Value)..]);
        foreach (Match stream in Regex.Matches(text, @"/Length (\d+) >>\nstream\n"))
        {
            var end = stream.Index + stream.Length + int.Parse(stream.Groups[1].Value);
            Assert.StartsWith("\nendstream", text[end..]);
        }
    }
}
