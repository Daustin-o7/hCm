using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Hospital_CRM.Tests;

public sealed class Part2FilesJobsLabTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", "opencode", "part2-files-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _order = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private static readonly byte[] Pdf = Encoding.UTF8.GetBytes("%PDF-1.4 synthetic private clinical report");

    private (LabFileStorage Files, IConfiguration Config) Storage(IDataProtectionProvider? keys = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Lab:UploadDir"] = "custom-reports" }).Build();
        return (new LabFileStorage(config, keys ?? DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
            new TestEnvironment { ContentRootPath = _root }), config);
    }

    private HospitalCrmDbContext Database()
    {
        var context = new TenantContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal(_tenant, "Doctor") } });
        var db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, context);
        db.LabOrders.Add(new LabOrder { Id = _order, TenantId = _tenant, DoctorId = _user, TestName = "Synthetic" });
        db.SaveChanges();
        return db;
    }

    private ClaimsPrincipal Principal(Guid? tenant, string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, _user.ToString()) };
        if (tenant.HasValue) claims.Add(new("tenant_id", tenant.Value.ToString()));
        if (role != null) claims.Add(new(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, role == null ? null : "test"));
    }

    private LabOrdersController Controller(HospitalCrmDbContext db, Guid? tenant, string? role, LabFileStorage? files = null)
    {
        var (storage, config) = Storage();
        return new LabOrdersController(db, config, files ?? storage)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenant, role) } } };
    }

    private static FormFile Upload(byte[]? bytes = null, string name = "report.pdf")
    {
        bytes ??= Pdf;
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }

    [Fact]
    public async Task Controller_upload_encrypts_custom_target_and_keeps_each_version_readable()
    {
        await using var db = Database();
        var controller = Controller(db, _tenant, "Doctor");
        for (var version = 1; version <= 2; version++)
        {
            var response = Assert.IsType<ObjectResult>(await controller.EnterResult(_order, null, false, null, null, Upload(), default));
            Assert.Equal(201, response.StatusCode);
            var body = JsonSerializer.SerializeToElement(response.Value);
            Assert.StartsWith($"/api/v1/lab-orders/{_order}/results/", body.GetProperty("fileUrl").GetString());
            Assert.Equal(version, body.GetProperty("version").GetInt32());
        }
        var results = await db.LabResults.OrderBy(r => r.Version).ToListAsync();
        Assert.Equal(results[0].Id, results[1].PreviousVersionId);
        Assert.All(results, r => Assert.StartsWith("private-lab:v1:", r.FileUrl));
        var disk = Directory.GetFiles(Path.Combine(_root, "custom-reports"), "*.enc", SearchOption.AllDirectories);
        Assert.Equal(2, disk.Length);
        foreach (var path in disk)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.AsSpan().IndexOf(Pdf) >= 0);
            Assert.StartsWith("HCLAB01", Encoding.ASCII.GetString(bytes[..7]));
        }
        // A fresh provider/storage instance simulates restart using the persisted key ring.
        controller = Controller(db, _tenant, "Nurse");
        foreach (var result in results)
        {
            var file = Assert.IsType<FileContentResult>(await controller.DownloadResult(_order, result.Id, default));
            Assert.Equal(Pdf, file.FileContents);
            Assert.Equal("no-store, private", controller.Response.Headers.CacheControl);
        }
    }

    [Theory]
    [InlineData("Receptionist", false, false)]
    [InlineData("Doctor", true, false)]
    [InlineData("Doctor", false, true)]
    [InlineData(null, false, false)]
    public async Task Download_denies_foreign_missing_tenant_wrong_role_and_anonymous(string? role, bool foreign, bool missing)
    {
        await using var db = Database();
        var result = new LabResult { Id = Guid.NewGuid(), TenantId = _tenant, LabOrderId = _order, FileUrl = "/lab-uploads/known.pdf", Version = 1 };
        db.LabResults.Add(result);
        await db.SaveChangesAsync();
        var controller = Controller(db, missing ? null : foreign ? Guid.NewGuid() : _tenant, role);
        var response = await controller.DownloadResult(_order, result.Id, default);
        Assert.True(response is UnauthorizedResult or ForbidResult or NotFoundObjectResult);
        Assert.False(Directory.Exists(Path.Combine(_root, "custom-reports")));
    }

    [Fact]
    public async Task Legacy_bytes_are_read_only_through_owned_record_without_rewriting()
    {
        await using var db = Database();
        var (storage, _) = Storage();
        Directory.CreateDirectory(storage.RootPath);
        var path = Path.Combine(storage.RootPath, "legacy.pdf");
        await File.WriteAllBytesAsync(path, Pdf);
        var result = new LabResult { Id = Guid.NewGuid(), TenantId = _tenant, LabOrderId = _order, FileUrl = "/lab-uploads/legacy.pdf", Version = 1 };
        db.LabResults.Add(result);
        await db.SaveChangesAsync();
        var controller = Controller(db, _tenant, "Doctor", storage);
        Assert.Equal(Pdf, Assert.IsType<FileContentResult>(await controller.DownloadResult(_order, result.Id, default)).FileContents);
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(path));
        Assert.Equal("/lab-uploads/legacy.pdf", result.FileUrl);
        Assert.IsType<NotFoundObjectResult>(await controller.DownloadResult(_order, Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData("/lab-uploads/../secret.pdf")]
    [InlineData("/lab-uploads/..%2fsecret.pdf")]
    [InlineData("/lab-uploads/C:\\secret.pdf")]
    [InlineData("https://foreign/report.pdf")]
    [InlineData("private-lab:v1:../../report.pdf")]
    [InlineData("private-lab:v1:../../11111111111111111111111111111111.pdf")]
    [InlineData("private-lab:v1:C:\\11111111111111111111111111111111.pdf")]
    [InlineData("private-lab:v1:/11111111111111111111111111111111.pdf")]
    public async Task Traversal_and_untrusted_locators_fail_closed(string locator)
    {
        var (storage, _) = Storage();
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadAsync(_tenant, _order, Guid.NewGuid(), locator, default));
    }

    [Fact]
    public async Task Invalid_stored_locator_is_a_controlled_download_failure()
    {
        await using var db = Database();
        var result = new LabResult { Id = Guid.NewGuid(), TenantId = _tenant, LabOrderId = _order,
            FileUrl = "private-lab:v1:../../11111111111111111111111111111111.pdf", Version = 1 };
        db.LabResults.Add(result);
        await db.SaveChangesAsync();
        var response = Assert.IsType<ObjectResult>(await Controller(db, _tenant, "Doctor").DownloadResult(_order, result.Id, default));
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task Ciphertext_tamper_and_copied_owner_path_cannot_decrypt()
    {
        var (storage, _) = Storage();
        var resultId = Guid.NewGuid();
        var locator = await storage.WriteAsync(_tenant, _order, resultId, Upload(), default);
        var source = Directory.GetFiles(storage.RootPath, "*.enc", SearchOption.AllDirectories).Single();
        var otherTenant = Guid.NewGuid();
        var destination = source.Replace(_tenant.ToString("N"), otherTenant.ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        await Assert.ThrowsAsync<CryptographicException>(() => storage.ReadAsync(otherTenant, _order, resultId, locator, default));
        var bytes = await File.ReadAllBytesAsync(source);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(source, bytes);
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => storage.ReadAsync(_tenant, _order, resultId, locator, default));
    }

    [Fact]
    public async Task Upload_validation_and_failure_never_create_result()
    {
        await using var db = Database();
        var controller = Controller(db, _tenant, "Doctor");
        Assert.IsType<BadRequestObjectResult>(await controller.EnterResult(_order, null, false, null, null, Upload(name: "report.exe"), default));
        Assert.IsType<BadRequestObjectResult>(await controller.EnterResult(_order, null, false, null, null, Upload([]), default));
        var huge = new FormFile(new MemoryStream(Pdf), 0, LabFileStorage.MaxFileSizeBytes + 1L, "file", "large.pdf");
        Assert.Equal(413, Assert.IsType<ObjectResult>(await controller.EnterResult(_order, null, false, null, null, huge, default)).StatusCode);
        Assert.Empty(await db.LabResults.ToListAsync());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Part2FilesJobs";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
