using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}"); checks++;
}
byte[] Docx(bool macro)
{
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        zip.CreateEntry("[Content_Types].xml");
        zip.CreateEntry("word/document.xml");
        if (macro) zip.CreateEntry("word/vbaProject.bin");
    }
    return stream.ToArray();
}
Check(FileValidation.Identify(".pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n"))?.Kind == "pdf", "PDF header accepted");
Check(FileValidation.Identify(".pdf", Encoding.ASCII.GetBytes("<script>bad</script>")) is null, "Renamed executable content rejected");
Check(FileValidation.Identify(".exe", Encoding.ASCII.GetBytes("%PDF-1.7")) is null, "Unlisted extension rejected");
Check(FileValidation.Identify(".png", [137,80,78,71,13,10,26,10])?.ContentType == "image/png", "PNG MIME type comes from server");
Check(FileValidation.Identify(".jpg", [255,216,255])?.Kind == "image", "JPEG signature accepted");
Check(FileValidation.Identify(".webp", "RIFF1234WEBP"u8.ToArray())?.Kind == "image", "WebP signature accepted");
Check(FileValidation.Identify(".webp", "RIFF1234WAVE"u8.ToArray()) is null, "Other RIFF formats rejected");
Check(FileValidation.Identify(".docx", Docx(false))?.Kind == "word", "DOCX ZIP structure accepted");
Check(FileValidation.Identify(".docx", Docx(true)) is null, "Macro entry rejected in DOCX");
Check(FileValidation.Identify(".docx", [80,75,3,4]) is null, "Truncated ZIP rejected");
Check(FileValidation.Identify(".doc", [208,207,17,224,161,177,26,225])?.Kind == "word", "Legacy OLE header accepted");
Check(FileValidation.Identify(".png", []) is null, "Empty content rejected");
var legacyBytes = Encoding.ASCII.GetBytes("legacy SQL file");
var localStore = new FileContentStore(new ConfigurationBuilder().Build());
var legacyFile = await localStore.DownloadAsync(new StoredFile { Content = legacyBytes }, CancellationToken.None);
Check(legacyFile.SequenceEqual(legacyBytes), "Legacy SQL file contents remain downloadable without Blob Storage");
Console.WriteLine($"{checks} checks passed.");
