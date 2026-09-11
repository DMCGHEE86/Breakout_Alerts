# BreakoutAlerts

A Windows desktop scanner that watches a list of tickers for opening-range breakouts, tells
you when one fires, shows you the option contracts worth considering, and lets you send the
order without leaving the app.

**It is an attention tool, not an auto-trader.** It never sizes a position, never decides to
trade, and never places an order on its own. Every order takes two deliberate clicks from you.

> ⚠️ **This software can place real orders against a real brokerage account.** It is a
> personal tool, not a licensed product, and nothing it displays is investment advice. Use the
> paper account until you trust it.

---

## What it does

**Scans** a watchlist on a timer. For each symbol it pulls 5-minute bars from your local
moomoo OpenD gateway, locks the opening range from the exact 09:30–09:45 window, and watches
for breakouts.

**Alerts** on three signals:

| Signal | Fires when |
|---|---|
| `PATH-1` | A candle body closes outside the opening range |
| `PATH-2` | After a counter-direction pullback retests the broken level, the next candle closes back beyond it — the continuation confirmation |
| `PATH-3` | A candle closes beyond the **premarket** high or low, but only when that level sits *outside* the opening range and is therefore a genuine second barrier |

**Ranks option contracts** for the alerted symbol — filtered to absolute delta 0.60–0.70 and
ranked by proximity to 0.65, with the top pick badged.

**Charts the setup** on double-click: one session, the opening range as a shaded band,
premarket levels, a marker at 9:30, and a flag on the candle that fired each signal.

**Routes the order** to moomoo — you pick the account, type the quantity, review, confirm.

---

## Requirements

- **Windows 10/11**
- **.NET 10 runtime** with Windows Desktop support
- **moomoo OpenD** installed, running, and logged in
- A moomoo account with US market data. A **paper account** is strongly recommended to start.

OpenD is the piece people forget. It is moomoo's local gateway — this app talks to it over
`127.0.0.1:11111` and never to moomoo directly. **If OpenD is not running, the app shows a
disconnected state and scans nothing.** It will not quietly substitute fake data.

---

## Getting started

```bash
git clone <your-repo-url>
cd BreakoutAlerts
dotnet build BreakoutAlerts.slnx -c Release
```

Start OpenD and log in, then run:

```
src\BreakoutAlerts.App\bin\Release\net10.0-windows\BreakoutAlerts.App.exe
```

The log at `%LOCALAPPDATA%\BreakoutAlerts\logs\` should show:

```
Loaded 9 watchlist symbols
Connected to OpenD at 127.0.0.1:11111
Replayed N alerts, M from today
```

Add symbols in the watchlist rail on the right. An unrecognised ticker is rejected when you
add it rather than sitting there as a silently empty row.

---

## Configuration

`src/BreakoutAlerts.App/appsettings.json`:

| Setting | Default | What it does |
|---|---|---|
| `Provider` | `Moomoo` | `Moomoo` or `SyntheticForTestingOnly` |
| `Host` / `Port` | `127.0.0.1` / `11111` | Where OpenD listens |
| `TimeframeMinutes` | `5` | Bar size. Only 1, 3, 5 and 15 are valid — see below |
| `ScanIntervalSeconds` | `60` | Seconds between scans |
| `QuotePollSeconds` | `5` | Watchlist price refresh |
| `AlertCutoffTime` | `14:00` | Latest time an alert is published. Blank publishes all day |
| `OptionTargetDelta` | `0.65` | Ranking target |
| `OptionMinimumDelta` / `OptionMaximumDelta` | `0.60` / `0.70` | Hard band; outside it a contract is excluded, not just ranked lower |

**Why only 1, 3, 5 and 15 minute bars?** They divide 15 evenly. On a 2-minute chart the
09:44–09:46 bar straddles the 09:45 lock and would silently widen the opening range. The app
refuses those timeframes rather than producing a plausible wrong number.

**Why a 2pm cutoff?** An opening-range breakout is a claim about the day's direction. One made
twenty minutes before the close leaves no time to act, so it competes for attention with
signals that still matter.

**Synthetic data** exists for offline development and is deliberately awkward to enable: the
config value is named `SyntheticForTestingOnly`, the default is live, a missing or malformed
config file lands on live, and when it is active the app shows a full-width banner and logs a
warning. An app that quietly serves invented prices while looking like it is serving real ones
is the most damaging thing this codebase could do.

---

## Order routing

**Scope is entry only.** This exists to save clicks timing an entry. Once you are in, you
manage the position in the moomoo app.

Placing an order takes two steps: `Review order` arms it and restates account, environment,
side, quantity, contract and price exactly as they will be sent; `CONFIRM AND SEND` is a
separate button. One-click ordering from a scanner is the interaction that turns a misread row
into a position.

Other guards:

- **Paper is preselected.** A live account is never chosen for you.
- **Switching accounts clears an armed confirmation** — it was for a different account.
- **Limit is the default**, priced at the mid. Market is opt-in and resets on every ticket,
  because option spreads are wide enough to fill well away from the quoted price.
- **Live trading is unlocked in OpenD, not here.** Click **Unlock Trading** at the top right of
  the OpenD window and enter your 6-digit trade password there. The GUI version of OpenD
  refuses unlocks through the API, and nothing in the API reports whether OpenD is unlocked —
  so if it isn't, the broker refuses the order and its message appears on the Trading page.
  Paper orders need no unlock, so the whole flow can be rehearsed without it.
- **Disabled and non-US accounts are not offered.** A gateway also exposes crypto and
  prediction-market accounts, and a picker that lets you send an options order to one is a trap.
- **Cash and margin are labelled.** Long options are paid in full, so `Cash` is the number
  that constrains the order — `Power` is the margin figure and is shown separately.

**This app handles no credentials at all.** OpenD holds your account login, and the trade
password is entered in OpenD's own window. Nothing in this repository asks for, stores or
transmits either.

---

## Where your data lives

`%LOCALAPPDATA%\BreakoutAlerts\`

| File | Contents |
|---|---|
| `alerts.jsonl` | Every signal, append-only. The substrate for future backtesting |
| `orders.jsonl` | Every order submitted, cancelled or modified, with account and environment |
| `watchlist.json` | Your symbols |
| `bars/` | Cached bars for completed sessions |
| `logs/` | Rolling application log, 14 days |

**None of this is in the repository, and `.gitignore` is written to keep it that way.** The
order log in particular contains broker account identifiers.

The alert log is append-only and never rewritten. Older records use an earlier field layout
and are translated when read.

---

## Project layout

```
src/
  BreakoutAlerts.Core/   net10.0          Domain, strategies, ranking, caching. No WPF.
  BreakoutAlerts.App/    net10.0-windows  WPF views, ViewModels, moomoo adapter, DI host.
test/
  BreakoutAlerts.Core.Tests/              xUnit + NSubstitute
tools/
  BreakoutAlerts.OpenDProbe/              Standalone gateway probe
.md/                                      Spec and running decision log
```

`Core` has no WPF and no moomoo dependency. That is what makes the strategies, session logic,
ranking and cache testable without a gateway or a window — and it is where essentially all the
tests live.

```bash
dotnet test test/BreakoutAlerts.Core.Tests/BreakoutAlerts.Core.Tests.csproj
```

---

## Diagnostic tools

The app's windows cannot be screenshotted from an automated shell, so several headless paths
exist. They were not conveniences — each one found shipping bugs.

```bash
BreakoutAlerts.App.exe --probe-live AAPL out.txt      # bar sources, snapshot, option chain, ranking
BreakoutAlerts.App.exe --probe-trade out.txt          # accounts, funds, positions. READ ONLY
BreakoutAlerts.App.exe --render-alert NVDA chart.png  # the popup for a real alert, as a PNG
BreakoutAlerts.App.exe --render-sample sample.png     # the renderer against a synthetic session
```

`--probe-trade` cannot place, modify or cancel anything.

---

## Gateway behaviours worth knowing

Each of these was measured against a live gateway, and each would otherwise have shipped as a
subtly wrong number rather than a visible failure. Full detail in `.md/plan.md.txt`.

- **`RequestHistoryKL`'s `EndTime` is exclusive.** Asking for "up to today" returns data to
  *yesterday's* close. Uncorrected, today's premarket is missing and `PATH-3` can never fire.
- **An over-long history response is truncated from the newest end** — you get a
  plausible-looking thousand bars with today's missing.
- **Bars are stamped with their close**, not their open. Passed through, the 09:30–09:45
  window captures two bars instead of three and the opening range is wrong every day.
- **`ExtendedTime` must be set** or there are no premarket bars at all.
- **The option chain window is capped at 30 days**; wider is rejected outright and returns an
  empty list.
- **The option delta filter takes signed bounds.** Puts have negative delta, so a positive-only
  band silently excludes every put.
- **`Currency` is required** on funds and positions, and changes the values returned.
- **`NeedGeneralSecAccount` defaults to false** and hides real securities accounts.
- **No overnight data exists.** Bars cover 04:00–20:00 only; 20:00–04:00 is unavailable at any
  timeframe.

---

## Known limitations

- **Telegram notifications and an installer are not built.** `dotnet publish` produces a
  folder, not an installer.
- **Order modify has no UI.** Cancel works; amending price or quantity must be done in moomoo.
- **The bar cache only accumulates going forward.** It cannot recover history it never saw, so
  charts for alerts older than roughly three days will be empty until the cache fills.
- **No backtesting yet.** The alert log is being built for it, but nothing reads it.
- **One strategy.** Some plumbing is still opening-range shaped and will be generalised as a
  second strategy lands.

---

## Licence and disclaimer

Personal project, provided as-is. Trading involves risk of loss. Nothing here is investment
advice, and the author is not a licensed adviser. You are responsible for every order this
software sends on your behalf.
