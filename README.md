# MabiCommerce NewLife

A Windows desktop planner for Mabinogi commerce. Compare goods, stock, transport, routes, and estimated returns across Trade, Group, and Barter.

## Screenshots

| Trade | Auto route rankings |
| --- | --- |
| <img src="images/ManualView.jpg" alt="Trade page with town prices and purchase controls" width="460"> | <img src="images/AutoView.png" alt="Ranked trade routes with estimated time and profit" width="460"> |

| Barter | Load Profit |
| --- | --- |
| <img src="images/BarterView.png" alt="Barter offers, weekly stock, and material cost" width="460"> | <img src="images/ProfitView.png" alt="Estimated return by destination for a loaded transport" width="460"> |

## What it does

- **Trade and Group:** enter prices and stock, build a transport load, and compare estimated returns by destination.
- **Barter:** review offers and weekly stock, track exchange materials, estimate returns, and copy a shopping list.
- **Route planning:** rank plans with Auto, compare destinations for a current load with Load Profit, and view selected routes on a map.
- **Price scanning:** select a screen area to read prices and stock. Validated readings may apply automatically; uncertain readings are sent for review.
- **Inventory and bonuses:** track transports, Ducats, letters, merchant rating, and commerce bonuses.
- **Optional price sharing:** enable sharing in Settings and accept the consent prompt to send accepted scans; refresh shared prices manually.
- **Built-in guide:** open the book icon for the user manual and tutorial.

## Get started

Download the latest [Windows release](https://github.com/Root50199/MabiCommerceNewLife/releases), extract the ZIP, and run `MabiCommerceNewLife.exe`. Keep the `Data` and `x64` folders beside the executable. The app includes the .NET runtime; OCR scanning also requires the [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe).

## Notes

Prices and stock are entered by you or read from a screen area you select; they are not live game data. Route times are estimates, and some routes may not have an estimate. Barter offers and game values can change, so check inputs against the game.

## Build and test

Requires the .NET 9 SDK on Windows.

```powershell
dotnet build .\MabiCommerceNewLife.csproj
dotnet test .\CalculatorTests\CalculatorTests.csproj
```

## Credits and license

Inspired by [MabiCommerce](https://github.com/Xcelled/mabicommerce). Route mapping uses ideas and formats from [Mabioned](https://github.com/exectails/Mabioned); OCR uses [Tesseract](https://github.com/tesseract-ocr/tesseract) and its .NET wrapper. The source code is MIT-licensed; see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Game artwork and other third-party assets retain their respective rights.
