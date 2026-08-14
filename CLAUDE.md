# CLAUDE.md — Early Signal Investment System

## Технологичен stack
- .NET 9, C#
- EF Core + SQL Server (LocalDB за dev)
- Hangfire за scheduled jobs
- Anthropic Claude API за AI анализ — AI Analyzer ползва claude-sonnet-5, Technical Assessor (OverboughtOversoldService) все още ползва claude-haiku-4-5 — изисква `Anthropic:ApiKey` (env var `Anthropic__ApiKey`)
- Alpha Vantage API за stock prices — изисква `AlphaVantage:ApiKey` от `appsettings.Development.json`
- MudBlazor v9.6.0 за UI компоненти (Blazor Web App)
- MAUI Hybrid shell — планиран за по-късно (мобилни приложения)

## Архитектурни конвенции
- Data access: директен `DbContext` injection в Services. Без Repository pattern.
- Naming: интерфейси `IXxxService`, имплементации `XxxService`. Blazor компоненти `Xxx.razor` (PascalCase).
- Структура на папки:
  - `/Data` — DbContext, EF migrations
  - `/Models` — entity класове
  - `/Services` — всички services
  - `/Components` — MudBlazor razor компоненти
  - `/Components/Pages` — Blazor страници (routable)
  - `/skills` — skill файлове за агента (в корена на repo-то, не вътре в проекта)
- Async навсякъде за I/O (`async`/`await`, `Task<T>`)
- API keys никога в код — само в `appsettings.Development.json` (в `.gitignore`) или environment variables

## Команди
- Build: `dotnet build` — Claude Code пуска автоматично след всяка промяна на код, без да пита.
- Тестове: `dotnet test` — само при изрична молба, не автоматично.
- EF migrations: `dotnet ef migrations add <Name>` / `dotnet ef database update`
- Стартиране: `dotnet run` или F5 във VS

## Skills за проекта
- Нов data collector → виж `/skills/signal-collector/SKILL.md`
- Нов AI prompt → виж `/skills/ai-analyzer/SKILL.md`
- Нов MudBlazor компонент → виж `/skills/mudblazor-component/SKILL.md`

## Идея и цел на системата
AI-powered инвестиционен скенер с дългосрочен хоризонт (2-5+ години). Не е trading бот.
Целта: намиране на компании ПРЕДИ пазарът да ги е открил, чрез синтез на сигнали от
множество независими източници (законодателство, бюджети, insider buying, обществени поръчки).

Философия: edge-ът идва не от скоростта на четене на публични данни, а от комбинирането
на сигнали, които никой алгоритъм не синтезира заедно.

## База данни — текущи таблици
- Signals — сурови сигнали от всички collector-и
- SectorScores — AI оценки по сектор
- CompanyPicks — компании идентифицирани от AI анализатора (с Ticker от Companies таблицата)
- CompanyPickSignals — many-to-many между CompanyPicks и Signals
- RunLogs — история на всички job изпълнения (JobName колона е ключова)
- CumulativeScores — агрегирани scores по компания за последните 14 дни
- ShortlistSnapshots — дневни snapshots на топ 5 компании
- TechnicalAssessments — Overbought/Oversold оценки от AI + технически индикатори
- Companies — master таблица с компании и техните борсови тикери

## Collector-и и Hangfire jobs schedule
| Job | Cron | Описание | Статус |
|-----|------|----------|--------|
| eur-lex-data-collector | 18:00 | EU Official Journal RSS | ✅ Работи |
| ep-parliament-collector | 18:05 | Draft committee доклади/становища (EP Open Data API) | ✅ Работи (виж по-долу) |
| sec-edgar-collector | 18:10 | Form 4 insider buying | ✅ Работи (0 покупки засега) |
| sec-edgar-13dg-collector | 18:12 | 13D/13G >5% придобивания | ✅ Работи |
| ted-collector | 18:15 | EU обществени поръчки | ✅ Работи |
| oecd-collector | 18:20 | OECD Composite Leading Indicator (месечен turning-point сигнал по държави) | ✅ Работи (виж по-долу) |
| amf-collector | 18:25 | Short selling register (AMF France, замества ESMA) | ✅ Работи (виж по-долу) |
| ai-signal-analyzer | 18:30 | Claude API анализ | ✅ Работи |
| ticker-verifier | 18:45 | SEC/AV/OpenFIGI/Yahoo ticker lookup | ✅ Работи |
| cumulative-scorer | 19:00 | Scoring engine | ✅ Работи |
| technical-assessor | 19:15 | Overbought/Oversold | ✅ Работи |

**Ред на веригата:** ticker-verifier е нарочно ПРЕДИ cumulative-scorer (и в cron-а, и в `/api/scan-now` ContinueJobWith веригата в Program.cs) — не е случаен избор. AI Analyzer-ът създава нови `Company` редове с `Ticker = null`; Cumulative Scorer чете `Companies.Ticker` "на живо" всеки run. Ако Scorer-ът мине първи, днешните нови компании остават без тикър/цена цял ден до утрешния цикъл (открито на живо 2026-08-14 — Stryker и Elastic имаха верифициран тикър, но shortlist-ът още показваше null, защото Ticker Verifier беше последна стъпка в старата верига). Правилният ред по зависимости е: collectors → AI Analyzer → **Ticker Verifier** → Cumulative Scorer → Technical Assessor.

## Scoring логика
Компания влиза в CumulativeScores ако:
- SignalDiversity >= 2 (поне 2 различни типа сигнали) → винаги включвай
- SignalDiversity == 1 AND SignalCount >= 2 → включвай
- SignalDiversity == 1 AND SignalCount == 1 → изключвай (шум)

Формула:
- Raw Score = BaseScore + (SignalDiversity × 20) + (SignalCount × 3) + VelocityBonus
- VelocityBonus: High (+10), Medium (+5), Low (+0)
- Нормализиран до 100

Ordering на топ 5 (Shortlist):
1. SignalDiversity DESC
2. SignalCount DESC
3. FirstSignalDate DESC (по-нова = по-добре)

## Ticker Verification логика
5-degrees pipeline, всяко ниво се пробва само ако предишното не намери нищо:
1. **SEC** (`company_tickers.json`) — безплатно, без ключ, без rate limit. Само US-listed компании. OTC ADR/foreign-ordinary тикъри (5 букви, завършващи на Y/F) се пазят отделно като last-resort fallback (стъпка 5), не се приемат директно тук.
2. **Alpha Vantage** SYMBOL_SEARCH — пробва се първо по `TickerHint` (ако AI Analyzer-ът е дал такъв), после по CompanyName. Предпочитани борси по ранг: NYSE/NASDAQ → XETRA/Frankfurt → London → Euronext (Paris/Amsterdam/Brussels) → Milan → Madrid → други европейски. OTC-suffix тикъри се приемат само като last-resort (по-лош ранг от всяка истинска борса). 25 заявки/ден общо (споделено и с RSI/MACD/цени) — тесното място.
3. **OpenFIGI** — много по-широко глобално покритие от AV, 5 заявки/мин без ключ (20/мин с `OpenFigi:ApiKey`, опционален). Пробва се първо по `TickerHint`, после по CompanyName ако hint-ът не даде нищо приемливо. **Важно**: филтрира по `securityType2 == "Common Stock"`, не само по `marketSector == "Equity"` — futures/options контракти на дадена акция ИСКАТ marketSector="Equity" в OpenFIGI-евата таксономия (объркващо), затова без този филтър bare ticker hint търсене може да върне Bloomberg-ов futures ticker (напр. "HUH1V=1" вместо реалната акция "HUH1V") — открито на живо 2026-08-14.
4. **Yahoo Finance** search (`query1.finance.yahoo.com/v1/finance/search`) — свободно, без ключ, без документиран дневен лимит, изисква `User-Agent` header (иначе 429). Филтрира по `quoteType == "EQUITY"`. Връща Yahoo-нативен symbol директно, вече съвместим с `YahooFinanceService` за цени/RSI/MACD без нужда от AV cross-check.
5. **Fallback**: SEC OTC тикър (ако има) → директно `TickerHint` от AI Analyzer-а, маркиран `Exchange = "AI suggested (unverified)"` → ако нищо: `TickerVerified = true`, `Ticker = null` (UI показва "No ticker found", опашката не блокира вечно).

Обработва максимум 15 компании на run (SEC/OpenFIGI/Yahoo fallback стъпките не пипат AV квотата).

**LSE International Order Book (IOB) тикъри се отхвърлят напълно.** Разпознаваем формат: започват с цифра, AV суфикс `.LON` (напр. `0QJS.LON`, `0K9W.LON`). За разлика от OTC ADR-и (реални, просто по-слабо ликвидни US сделки), IOB duplicate listings често напълно спират да търгуват — потвърдено на живо: Clariant и Huhtamaki и двете имаха такъв тикър, без нито една реална сделка от 2026-07-17 нататък, докато основните им борси (Xetra/Milan) вървяха нормално. `IsLikelyIobTicker` филтърът в `SearchSymbolAsync` ги маха преди `PickBest`, за да не заклещи компанията завинаги зад "цена = null" (по-добре `TickerVerified = false` и retry следващия run).

**Ticker freshness check** (`IsTickerLiquidAsync`, добавен 2026-08-14): преди да се приеме кандидат от SEC-тира или от AV/OpenFIGI/Yahoo `best` резултата, проверяваме през `YahooFinanceService.GetDailyClosesAsync`, че последната реална сделка е в рамките на `StaleTickerThresholdDays` (14 дни) — иначе кандидатът се третира като "не намерен" и пайплайнът пада към следващия tier. Открито на живо: Sealed Air Corporation (`SEE`) имаше валиден формат тикър, но Alpha Vantage-овите данни не бяха опреснявани от 2026-04-09, а Yahoo връщаше "symbol may be delisted" — компанията реално е спряла да се търгува преди месеци, не bug в пайплайна. За вече верифицирани тикъри, които останат без цена въпреки това (напр. верифицирани преди тази проверка да съществува), UI-то показва "⚠ No live price" вместо тихо празно поле — виж `Shortlist.razor`/`Home.razor`.

## Late Detection логика
Сигнализира, че пазарът вероятно вече реагира на сигнала, преди системата да е действала по него — намалява реалния "edge" на pick-а, дори score-ът да е висок. Изчислява се в `CumulativeScoringService.DetectLateSignalAsync`, само за top 5 (същия обхват като ценовото обогатяване), пази се в `CumulativeScores.LateDetectionFlag`/`LateDetectionReason`, показва се като "⏱ Late detection" chip на Shortlist.razor и Home.razor Quick Shortlist картите.
Два независими признака, всеки достатъчен сам по себе си:
1. **Price reaction** — `|PriceChangePercent| >= 8%` спрямо цената при first signal.
2. **Volume spike** — среден обем за последните 3 търговски дни >= 2.0x средния обем за предходните 20 дни (данни от `YahooFinanceService.GetDailyVolumesAsync`, добавен успоредно с `GetDailyClosesAsync` — двата споделят един и същ chart fetch по тикър, кеширан в instance-а).
**Media coverage частта от оригиналната идея е спряна (dropped), няма да се имплементира.** Изследвано на живо (2026-08-14): Google News RSS (`news.google.com/rss/search`) е свободен, без ключ, но за активно търгувана компания (Elanco) връща 90+ статии, почти изцяло рутинно финансово отразяване (earnings, insider-trade ботове като MarketBeat/Stock Titan, analyst targets) без връзка с конкретния сигнал, заради който компанията е избрана. Просто броене на статии след first signal би сработвало като шум, не сигнал — би "гърмяло" за всяка добре отразявана компания и никога за наистина нишова. Смислена версия би изисквала targeted search (име на компания + ключова дума от AI rationale-а), оценено като прекалено усложнение за момента.

## EP Parliament Collector логика
Извиква EP Open Data API v2 (`data.europarl.europa.eu/api/v2/committee-documents/feed`) — свободен достъп, без ключ, лимит 500 заявки/5мин. Feed-ът връща Atom записи за draft committee доклади/становища, публикувани или обновени през последния месец.
Това е най-ранният сигнал в системата: комисиите изготвят тези draft документи МЕСЕЦИ преди финалното приемане и публикуване в EUR-Lex Official Journal (който хваща само финалното, вече прието законодателство).
Title-ът включва committee код-префикс (напр. `[ECON]`, `[ITRE]`), извлечен от document ID-то, без нужда от отделен lookup endpoint.
**Важно**: заявката изисква `User-Agent` header — технически "по избор" в OpenAPI спецификацията, но без него API-то връща 403 (потвърдено на живо, 2026-08-05).

## AMF Collector логика (замества ESMA)
ESMA премести собствения си net-short-position регистър зад CAPTCHA-защитен портал (registers.esma.europa.eu) — потвърдено на живо (2026-08-05), и главната търсачка, и всеки export вика captcha endpoint. Bypass-ване на CAPTCHA е забранено.
Вместо pan-EU агрегатора минахме на **AMF France** (Autorité des marchés financiers — National Competent Authority под същия EU Short Selling Regulation) — публикуват свободно, без CAPTCHA, дневно обновяван CSV през data.gouv.fr: `https://www.data.gouv.fr/api/1/datasets/r/c2539d1c-8531-4937-9cba-3bd8e9786cc5`.
По-тесен обхват от преди (само FR-листнати емитенти вместо целия ЕС), но реален и работещ източник.
Файлът е пълен history log (всяка промяна на ratio-то е отделен ред), не текущ snapshot — затова взимаме само реда с най-скорошна "Date de debut position" за всяка двойка (holder, ISIN) и филтрираме по празна "Date de fin de publication position" (= позицията още не е затворена). Инак стари редове се сумират многократно (открито на живо: FORVIA излизаше 323% вместо реалните 4.6%).

## OECD Collector логика
Извиква OECD SDMX API (`OECD.SDD.STES,DSD_STES@DF_CLI,4.1` — Composite Leading Indicator, амплитудно-коригиран, месечен) за 8 държави (US/DE/FR/UK/JP/CN/IT/ES, покриващи основните борси от Ticker Verification pipeline-а). CLI = 100 е дългосрочният тренд; индикаторът е проектиран да сигнализира обръщания на цикъла 4-8 месеца преди да се видят в реалните икономически данни.
Сигнал се генерира само когато последната месечна стойност пресече прага от 100 спрямо предходния месец (нагоре или надолу) — не при всяко малко колебание, за да няма шум. `DF_CLI` версия 4.0 връща празни данни на sdmx.oecd.org (маркирана `NonProductionDataflow`) — 4.1 е активната версия, проверено на живо (2026-08-05).

## AI Analyzer конвенции
- Model: claude-sonnet-5 (сменен от claude-haiku-4-5 — по-добро instruction-following при отхвърляне на частни/нереални компании, разликата в разход е пренебрежима при текущия обем)
- Batch size: 15 сигнала
- Prompt изисква: реални публично търгувани компании на NYSE/NASDAQ/LSE/XETRA/Euronext
- НЕ приема: категории, правителствени агенции, частни subsidiary-та, описания
- Максимум 5 компании на batch
- Връща: companyName, sector, score, sentiment (Bullish/Bearish/Neutral), rationale, signalIds
- НЕ връща тикър — тикърът идва от Companies таблицата

## UI страници
- / (Home) — Quick Shortlist (топ 3) + Sector cards
- /shortlist — Пълен shortlist топ 5 с детайли
- /insiders — SEC EDGAR Form 4 insider покупки
- /history — Дневни snapshots на shortlist-а (групирани по дата)
- /technical — линкнат като "Dashboard" в header-а (не в sidebar-а), Pipeline Stages (5 stage-card-а: Signal Collection → AI Analysis → Ticker Resolution → Scoring & Shortlisting → Enrichment, всеки съдържа вложени job card-ове; auto-refresh на всеки 5 секунди) + scan история
- /hangfire — Hangfire dashboard

Sidebar ред: Home → Shortlist → Insiders → History → бутон "Scan Now". "Dashboard" (/technical) е в header-а горе вдясно, преди "About", не в sidebar-а.

## Scan Now бутон
Изложен през `POST /api/scan-now`, който отвътре ползва `IBackgroundJobClient` (не директно enqueue-нат от UI).
Използва `ScanGate.Semaphore` (SemaphoreSlim 1,1) за защита срещу паралелни scans.
15-минутен safety-net release при failed chain.

## Известни проблеми (предстои поправка)
Няма в момента.

## Решени проблеми (за история)
- Overbought/Oversold badge — вече се показва на Shortlist.razor (беше вече wired, само чакаше данни) и добавен на Home.razor Quick Shortlist картите за паритет; tooltip с AI-generated Reason (2026-08-14)
- Ticker freshness check — нови тикър кандидати вече се проверяват срещу Yahoo дневни данни преди да се приемат (`IsTickerLiquidAsync`, праг 14 дни); "мъртъв" тикър (напр. открито на живо: Sealed Air "SEE" — AV заседнал от 2026-04-09, Yahoo "may be delisted") вече не блокира компанията завинаги, пада към следващия tier. За вече верифицирани тикъри, които останат без цена, Shortlist/Home показват "⚠ No live price" chip вместо тихо празно поле (2026-08-14)
- Late Detection (price reaction + volume spike) — виж "Late Detection логика"; тествано на живо, засече реален случай (Elanco, -11.8% от first signal) (2026-08-14)
- "Companies: 0" в Scan History — проверено (2026-08-05): логиката е коректна,брои легитимно нулеви резултати, не бъг
- UI scroll бъг — проверено (2026-08-05) на 3 viewport размера (Shortlist + Technical), скролва нормално навсякъде, вероятно поправено странично от render mode фикса
- Tooltip на Velocity/Source Types chip-овете — добавени (Shortlist.razor, Home.razor), Source Types показва реалните имена на типовете сигнали при hover
- Дата без час в Scan History — оправено, вече показва дата + час в local timezone (виж `Extensions/DateTimeExtensions.cs`)
- NULL тикери — 4-degree pipeline (SEC → Alpha Vantage → OpenFIGI → AI hint fallback), виж секция "Ticker Verification логика"
- Цени на shortlist картите — First/Latest signal @ price + ▲/▼ % добавени и на Shortlist.razor, и на Home.razor Quick Shortlist картите (2026-08-05)
- AI Analyzer връщаше companyName замърсен с дисквалифициращи бележки (напр. "Kemin Industries (private equity backed - NOT ELIGIBLE)") — промптът вече изрично забранява паренетични бележки в companyName; добавен е и defense-in-depth филтър, който прескача picks с "NOT ELIGIBLE" в името (2026-08-05)
- OECD collector — преминат от годишни government-deficit данни (безполезни, шум) на месечен Composite Leading Indicator turning-point detector, виж "OECD Collector логика"; тествано на живо, засече реален turning point за Италия (2026-08-05)
- ESMA collector — регистърът на ESMA-то е зад CAPTCHA (потвърдено, не bypass-ваме); заменен с AMF France, виж "AMF Collector логика"; тествано на живо, включително корекция на holder-history агрегационен бъг (2026-08-05)
- Нов EP Parliament collector — draft committee доклади/становища, месеци преди EUR-Lex публикация; виж "EP Parliament Collector логика"; тествано на живо, 279 реални сигнала при първия run (2026-08-05)

## Предстои да се имплементира
### UI
- History страница — разпъване при клик работи, но History е празна (нужни са дни данни)

### Infrastructure
- Хостинг — Railway или Azure (приложението работи само докато VS е пуснат)
- MAUI Hybrid мобилно приложение

## Забранени зони
- Никога API keys в код — само чрез `appsettings.Development.json` (в `.gitignore`) или environment variables
- Не пипа `appsettings.Production.json` директно — само през конфигурация при deploy
- При PowerShell команди — без bash heredoc синтаксис
- Git commit message — само с отделни -m флагове
