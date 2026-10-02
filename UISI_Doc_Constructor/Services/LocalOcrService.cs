using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor.Services;

public sealed class LocalOcrService
{
    private static readonly Lazy<bool> RuntimeLoaded = new(LoadRuntime);
    private static readonly HashSet<string> Languages = ["auto", "ukr", "rus", "eng"];

    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(BitmapSource image, IReadOnlyList<System.Windows.Rect> masks,
        string language, CancellationToken cancellationToken = default)
    {
        if (!Languages.Contains(language)) throw new ArgumentException("Unsupported OCR language.", nameof(language));
        // Frozen images can safely cross the UI/worker-thread boundary. No files or network are used.
        var prepared = ApplyMasks(image, masks);
        return Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = RuntimeLoaded.Value;
            var dataPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr", "tessdata");
            foreach (var code in (language == "auto" ? "eng+ukr+rus" : language).Split('+'))
                if (!File.Exists(Path.Combine(dataPath, code + ".traineddata")))
                    throw new InvalidOperationException("Не найдены языковые модели. Переустановите полную сборку программы.");

            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(prepared));
            encoder.Save(stream);
            using var pix = Pix.LoadFromMemory(stream.ToArray());
            var words = Recognize(pix, dataPath, language == "auto" ? "eng+ukr+rus" : language, cancellationToken);
            if (language == "auto")
            {
                // Multilingual OCR may confuse visually similar Latin/Cyrillic glyphs. Compare
                // aligned word candidates from each model, retaining the page's reading order.
                var candidates = new List<OcrWord>();
                foreach (var code in new[] { "ukr", "rus", "eng" })
                    candidates.AddRange(Recognize(pix, dataPath, code, cancellationToken));
                for (var i = 0; i < words.Count; i++)
                {
                    var original = words[i];
                    var best = candidates.Where(candidate => SameWordArea(original.Bounds, candidate.Bounds))
                        .Append(original).MaxBy(Quality)!;
                    if (Quality(best) > Quality(original) + 2)
                        words[i] = new OcrWord { Text = best.Text, Bounds = original.Bounds, Line = original.Line,
                            Confidence = best.Confidence, IsDictionaryWord = best.IsDictionaryWord };
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return words;
        }, cancellationToken);
    }

    private static float Quality(OcrWord word) => word.Confidence + (word.IsDictionaryWord ? 12 : 0);

    private static bool SameWordArea(System.Windows.Rect first, System.Windows.Rect second)
    {
        var intersection = System.Windows.Rect.Intersect(first, second);
        if (intersection.IsEmpty) return false;
        var intersectionArea = intersection.Width * intersection.Height;
        return intersectionArea / (first.Width * first.Height + second.Width * second.Height - intersectionArea) >= 0.65;
    }

    private static List<OcrWord> Recognize(Pix pix, string dataPath, string language, CancellationToken cancellationToken)
    {
            cancellationToken.ThrowIfCancellationRequested();
            using var engine = new TesseractEngine(dataPath, language, EngineMode.LstmOnly);
            engine.SetVariable("user_defined_dpi", "150");
            using var page = engine.Process(pix, PageSegMode.Auto);
            using var iterator = page.GetIterator();
            var words = new List<OcrWord>();
            var line = -1;
            iterator.Begin();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (iterator.IsAtBeginningOf(PageIteratorLevel.TextLine)) line++;
                var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (!string.IsNullOrEmpty(text) && iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                    words.Add(new OcrWord
                    {
                        Text = text,
                        Line = Math.Max(0, line),
                        Bounds = new System.Windows.Rect(bounds.X1, bounds.Y1, bounds.Width, bounds.Height),
                        Confidence = iterator.GetConfidence(PageIteratorLevel.Word),
                        IsDictionaryWord = iterator.GetWordIsFromDictionary()
                    });
            } while (iterator.Next(PageIteratorLevel.Word));
            cancellationToken.ThrowIfCancellationRequested();
            return words;
    }

    public static BitmapSource ApplyMasks(BitmapSource image, IEnumerable<System.Windows.Rect> masks)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        foreach (var mask in masks)
        {
            var left = (int)Math.Clamp(Math.Floor(mask.Left), 0, width);
            var right = (int)Math.Clamp(Math.Ceiling(mask.Right), 0, width);
            var top = (int)Math.Clamp(Math.Floor(mask.Top), 0, height);
            var bottom = (int)Math.Clamp(Math.Ceiling(mask.Bottom), 0, height);
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                {
                    var offset = y * stride + x * 4;
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
                }
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static bool LoadRuntime()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Распознавание доступно в 64-разрядной сборке программы (win-x64).");
        // App-local redistributables avoid requiring a separate VC++ installation on another laptop.
        foreach (var name in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "concrt140.dll" })
            NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, name));
        return true;
    }
}
