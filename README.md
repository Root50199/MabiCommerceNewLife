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
- **Boat timers:** boarding/departure countdowns, optionally shown only with an active route and limited to its boats. The timer body is click-through.
- **Departure checklist:** general or route-specific reminders for summons, Grandmaster, letters, gear/titles, cover, speed pots, Bandit Candy, Arcana and Group destination. Gear checks all configured equipment together; successful sales reset checks.
- **Price scanning:** select a screen area to read prices and stock. Validated readings may apply automatically; uncertain readings are sent for review.
- **Inventory and bonuses:** track transports, Ducats, letters, merchant rating, and commerce bonuses.
- **Optional price sharing:** enable sharing in Settings and accept the consent prompt to send accepted scans; refresh shared prices manually. Refresh also selects each outpost's newest scanned rotating barter offer, keeping newer local picks or scans.
- **Built-in guide:** open the book icon for the illustrated manual and tutorial, including previews of both overlays.

## Get started

Download the latest [Windows release](https://github.com/Root50199/MabiCommerceNewLife/releases), extract the ZIP, and run `MabiCommerceNewLife.exe`. Keep the `Data` and `x64` folders beside the executable. The app includes the .NET runtime; OCR scanning also requires the [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe).

## Notes

### Overlays

Enable **Boat timer overlay** or **Checklist overlay** in **Settings > General**; both start off. Each has **Only show with an active route**, with a nested relevant-boats/items option. An active route means loaded cargo with a chosen destination.

Hover over an overlay or the space just above it for its bar: drag to move, adjust background transparency, **- / +** to minimize/restore, or **X** to disable. Double-click the title/empty bar also minimizes/restores. Minimized bars stay visible; options and positions are saved.

Checklist **Click-through** passes clicks through text/background while keeping checkbox squares usable. Gear lists the configured chest piece, accessories and title on separate lines; its checkbox checks them all. Routed reminders name the selected letter and needed Alpaca/partner/William and omit unused cover. Checks last for the session and survive overlay reopening; successful sales reset them, but **Clear Load** does not.

### Estimates

Prices and stock are entered by you or read from a screen area you select; they are not live game data. Route times are estimates, and some routes may not have an estimate. Barter offers and game values can change, so check inputs against the game.

## Build and test

Requires the .NET 9 SDK on Windows.

```powershell
dotnet build .\MabiCommerceNewLife.csproj
dotnet test .\CalculatorTests\CalculatorTests.csproj
```

## Credits and license

Inspired by [MabiCommerce](https://github.com/Xcelled/mabicommerce). Route mapping uses ideas and formats from [Mabioned](https://github.com/exectails/Mabioned); OCR uses [Tesseract](https://github.com/tesseract-ocr/tesseract) and its .NET wrapper. The source code is MIT-licensed; see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Game artwork and other third-party assets retain their respective rights.
