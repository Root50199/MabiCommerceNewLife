# Test fixtures

Game screenshots are not committed. Put your own captures in `Fixtures/Ocr/`, which is git-ignored. Every PNG under `Fixtures/` is copied to the test output folder.

To add an OCR regression test:

1. In the game, open a Trading Post price list. Capture a region containing the bracket corner at the top left, the good's name, and the full town table. This is the same region you select for **SCAN**.
2. Save it as a PNG in `CalculatorTests/Fixtures/Ocr/`. Use the capture exactly as taken; resizing blurs the digits.
3. Copy `OcrCaptureTemplateTests.cs` and add a `DataRow` for your capture: the file name, the good's catalog name, its source buy price, and the town prices you can see.
4. Run `dotnet test .\CalculatorTests\CalculatorTests.csproj`.

Tests whose capture is missing report **Inconclusive** instead of failing, so a fresh clone still passes.
