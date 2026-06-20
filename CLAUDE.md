# CLAUDE.md — Early Signal Investment System

## Технологичен stack
- .NET 9, C#
- EF Core + SQL Server (LocalDB за dev, прави connection string-а конфигурируем)
- Hangfire за scheduled jobs
- Anthropic Claude API за AI анализ (изисква ANTHROPIC_API_KEY от environment, никога hardcoded)
- MudBlazor за UI компоненти (Blazor Web App; MAUI Hybrid shell добавяме по-късно)

## Архитектурни конвенции
- Data access: директен DbContext injection в Services. Без Repository pattern - проектът е малък, един разработчик.
- Naming: интерфейси IXxxService, имплементации XxxService. Blazor компоненти Xxx.razor (PascalCase).
- Структура на папки:
  - /Data - DbContext, EF migrations
  - /Models - entity класове (Signal, SectorScore, CompanyPick, RunLog)
  - /Services - DataCollectorService, AiAnalyzerService, DbWriterService
  - /Components - MudBlazor razor компоненти
  - /Pages - Blazor страници (routable)
- Async навсякъде за I/O (async/await, Task<T>) - collector-ите правят HTTP заявки, AI analyzer прави API call.

## Команди
- Build: dotnet build - Claude Code пуска автоматично след всяка промяна на код, без да пита.
- Тестове: dotnet test - пуска се само при изрична молба, не автоматично.
- EF migrations: dotnet ef migrations add <Name> / dotnet ef database update

## Забранени зони
- Никога не commit-ва API keys, connection strings, или secrets в код. Винаги през appsettings.Development.json (в .gitignore) или environment variables.
- Не пипа appsettings.Production.json директно - само през конфигурация при deploy.

## Skills за проекта
- Нов data collector (нов източник за сигнали) - виж /skills/signal-collector
- Нов AI prompt към Claude API за анализ - виж /skills/ai-analyzer
- Нов MudBlazor компонент - виж /skills/mudblazor-component

## Контекст на проекта
AI-powered инвестиционен скенер с дългосрочен хоризонт. Не trading бот. Събира сигнали от доверени източници (EUR-Lex, SEC EDGAR, Financial Modeling Prep, OECD, Reuters RSS), анализира ги през Claude API, генерира sector scores и company picks, показва ги в MudBlazor dashboard.
