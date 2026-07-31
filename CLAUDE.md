# CLAUDE.md — Early Signal Investment System

## Технологичен stack
- .NET 9, C#
- EF Core + SQL Server (LocalDB за dev)
- Hangfire за scheduled jobs
- Anthropic Claude API за AI анализ (claude-haiku-4-5) — изисква `ANTHROPIC_API_KEY` от environment
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
| sec-edgar-collector | 18:10 | Form 4 insider buying | ✅ Работи (0 покупки засега) |
| sec-edgar-13dg-collector | 18:12 | 13D/13G >5% придобивания | ✅ Работи |
| ted-collector | 18:15 | EU обществени поръчки | ✅ Работи |
| esma-collector | 18:25 | Short selling register | ⚠️ Активен, но неуспешен (CAPTCHA блокира endpoint-а) |
| ai-signal-analyzer | 18:30 | Claude API анализ | ✅ Работи |
| cumulative-scorer | 19:00 | Scoring engine | ✅ Работи |
| technical-assessor | 19:15 | Overbought/Oversold | ✅ Работи |
| ticker-verifier | 20:00 | Alpha Vantage ticker lookup | ✅ Работи |
| oecd-collector | — | Бюджетни данни | ❌ Деактивиран (исторически данни) |

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
Alpha Vantage SYMBOL_SEARCH с предпочитани борси:
1. NYSE, NASDAQ (United States)
2. XETRA, Frankfurt (Germany)
3. London Stock Exchange (United Kingdom)
4. Euronext (France, Netherlands, Belgium)
5. Borsa Italiana (Italy)
6. Madrid Stock Exchange (Spain)
7. Други европейски
8. Всички останали (Индия, Бразилия, OTC) → НЕ приемай

Обработва максимум 15 компании на run за да пести 25 заявки/ден квотата.

## AI Analyzer конвенции
- Model: claude-haiku-4-5
- Batch size: 15 сигнала
- Prompt изисква: реални публично търгувани компании на NYSE/NASDAQ/LSE/XETRA/Euronext
- НЕ приема: категории, правителствени агенции, частни subsidiary-та, описания
- Максимум 5 компании на batch
- Връща: companyName, sector, score, sentiment (Bullish/Bearish/Neutral), rationale, signalIds
- НЕ връща тикър — тикърът идва от Companies таблицата

## UI страници
- / (Home) — Quick Shortlist (топ 3) + Sector cards
- /shortlist — Пълен shortlist топ 5 с детайли
- /history — Дневни snapshots на shortlist-а (групирани по дата)
- /insiders — SEC EDGAR Form 4 insider покупки
- /technical — Canal статус + scan история
- /hangfire — Hangfire dashboard

## Scan Now бутон
Изложен през `POST /api/scan-now`, който отвътре ползва `IBackgroundJobClient` (не директно enqueue-нат от UI).
Използва `ScanGate.Semaphore` (SemaphoreSlim 1,1) за защита срещу паралелни scans.
15-минутен safety-net release при failed chain.

## Известни проблеми (предстои поправка)
1. "Companies: 0" в Scan History таблицата — грешна логика за броене
2. Дата без час в Scan History — трябва дата + час в local timezone
3. UI scroll бъг — съдържанието в долната част е отрязано
4. Tooltip на Velocity chip — липсва обяснение на логиката
5. Tooltip на Source Types chip — трябва "X/5 типа" с имена при hover
6. Цени на картите — Alpha Vantage имплементиран но тикерите са NULL за повечето компании
7. Overbought/Oversold badge — зависи от цените, засега не се показва

## Предстои да се имплементира
### Data
- OECD поправка — намери актуален endpoint за текущи бюджетни данни (не исторически)
- ESMA алтернатива — намери endpoint без CAPTCHA за short selling данни
- EU Parliament комитети — pre-legislative сигнали (много ранни)
- Late Detection checks — price reaction, volume spike, media coverage

### UI
- Цени на shortlist картите: FirstSignalDate @ $XX.XX, Latest: $XX.XX, ▲/▼ X.X%
- Overbought/Oversold badge с reason tooltip
- Tooltips на chips
- Timezone fix
- Scroll fix
- History страница — разпъване при клик работи, но History е празна (нужни са дни данни)

### Infrastructure
- Хостинг — Railway или Azure (приложението работи само докато VS е пуснат)
- MAUI Hybrid мобилно приложение

## Забранени зони
- Никога API keys в код — само чрез `appsettings.Development.json` (в `.gitignore`) или environment variables
- Не пипа `appsettings.Production.json` директно — само през конфигурация при deploy
- При PowerShell команди — без bash heredoc синтаксис
- Git commit message — само с отделни -m флагове
