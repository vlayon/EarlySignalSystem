# Skill: Нов MudBlazor компонент

Използвай този skill, когато добавяш нов MudBlazor компонент/страница в `Components/`, за да следва визуалната и структурна конвенция на dashboard-а (`Components/Pages/Home.razor`).

## Цветова схема

Дефинирана централно в `Services/EarlySignalTheme.cs` като `static MudTheme`, приложена в `Components/Layout/MainLayout.razor` през `<MudThemeProvider Theme="EarlySignalTheme.Theme" IsDarkMode="true" />`. Никога не hardcode-вай hex стойности директно в компонент — реферирай `EarlySignalTheme` константите или използвай MudBlazor `Color` enum-а (`Color.Primary`, `Color.Success`...), който резолвва към темата автоматично.

```csharp
public static class EarlySignalTheme
{
    public const string ColorBackground = "#0f1117";     // фон на страницата
    public const string ColorCardBackground = "#1a1d2e"; // MudThemeProvider Surface / MudCard фон
    public const string ColorPrimary = "#6366f1";         // indigo accent
    public const string ColorSuccess = "#22c55e";
    public const string ColorWarning = "#f59e0b";
    public const string ColorDanger = "#ef4444";
}
```

`PaletteDark` мапва тези към `Background`, `Surface`, `AppbarBackground`, `DrawerBackground`, `Primary`, `Success`, `Warning`, `Error`, плюс ръчно зададени `TextPrimary = "#e5e7eb"` и `TextSecondary = "#9ca3af"` за четим текст върху тъмен фон. Шрифтът е Inter, зареден през Google Fonts в `App.razor` и зададен в темата през `Typography.Default.FontFamily`.

Ако компонентът има вложени карти (напр. company card вътре в sector card), следващото ниво получава леко по-светъл фон от родителя си, за да се вижда йерархията — виж `#1a1d2e` (sector card) → `#20243a` (company card) в `Home.razor`. Задава се inline през `Style="background:#20243a;"` на конкретния `MudCard`, не през тема (защото е ниво-специфично, не global palette color).

## MudCard структура

Всяка секция е `MudCard`, с header за заглавие+badge и content за тялото:

```razor
<MudCard Class="mb-6" Style="background:#1a1d2e;">
    <MudCardHeader>
        <CardHeaderContent>
            <MudStack Row="true" Spacing="3" AlignItems="AlignItems.Center">
                <MudText Typo="Typo.h5" Style="font-weight:600;">@sector.SectorName</MudText>
                <MudChip T="string" Color="@GetScoreColor(sector.Score)" Variant="Variant.Filled" Size="Size.Small">
                    @sector.Score.ToString("0.0")
                </MudChip>
            </MudStack>
        </CardHeaderContent>
    </MudCardHeader>
    <MudCardContent>
        @* съдържание *@
    </MudCardContent>
</MudCard>
```

Вложените (child) карти нямат `MudCardHeader` — заглавие/badge и съдържание са directly в `MudCardContent`, за по-компактен layout при малки карти (виж company card-а).

## MudChip за score badges

`MudChip` изисква explicit `T="string"` (generic компонент). Цветът винаги идва от helper метод, никога inline conditional в markup-а:

```csharp
private static Color GetScoreColor(decimal score) => score switch
{
    >= 70 => Color.Success,
    >= 40 => Color.Warning,
    _ => Color.Error,
};
```

```razor
<MudChip T="string" Color="@GetScoreColor(sector.Score)" Variant="Variant.Filled" Size="Size.Small">
    @sector.Score.ToString("0.0")
</MudChip>
```

Конвенция за прагове (приложи същата логика за всеки нов score/confidence badge): `>= 70` зелено (`Success`), `40–69` жълто (`Warning`), `< 40` червено (`Error`). `Variant.Filled` + `Size.Small` навсякъде за badge-и — не менявай variant/size без причина, за консистентност между sector и company ниво.

## MudGrid / MudItem за карти в решетка

Дъщерни карти на секция (напр. компаниите в даден сектор) се подреждат през `MudGrid` + `MudItem` с responsive breakpoints, не с ръчен CSS flex/grid:

```razor
<MudGrid>
    @foreach (var pick in picks)
    {
        <MudItem xs="12" sm="6" md="4">
            <MudCard Style="background:#20243a; height:100%;">
                ...
            </MudCard>
        </MudItem>
    }
</MudGrid>
```

`xs="12" sm="6" md="4"` = 1 карта на ред на мобилен, 2 на таблет, 3 на десктоп. `height:100%"` на вложената `MudCard`, за да излязат еднакво високи в един ред независимо от дължината на съдържанието.

## Spacing конвенции

- Разстояние между главните секции (напр. между sector cards): `Class="mb-6"` на `MudCard`.
- Разстояние между елементи вътре в `MudStack`: `Spacing="3"` за хоризонтални групи (заглавие + badge), `Spacing="0"` когато елементите трябва да залепнат plътно (напр. ticker над company name).
- Разстояние преди текстов блок след друг елемент: `Class="mt-3"` (напр. rationale текст под header реда на company card-а).
- Контейнер на цялата страница: `<MudContainer MaxWidth="MaxWidth.Large" Class="py-6">`.
- Layout на header реда на страницата (заглавие + мета информация от дясно): `<MudStack Row="true" Justify="Justify.SpaceBetween" AlignItems="AlignItems.Center" Class="mb-6">`.

Не добавяй custom margin/padding CSS класове извън тези utility class-ове (`mb-*`, `mt-*`, `py-*`), освен ако MudBlazor няма вграден начин — всичко на страницата минава през `MudStack`/`Class` spacing, не raw `<div style="margin...">`.

## Pattern за expand/collapse (inline, не dialog/modal)

Company карта показва съкратен текст по подразбиране, с `MudIconButton` за разгъване inline (не отваря dialog, не навигира):

**State**: `HashSet<int>` от разгънатите ID-та, вместо `bool` флаг на всеки ред — компактно за произволен брой карти:

```csharp
private readonly HashSet<int> _expandedIds = [];

private bool IsExpanded(int pickId) => _expandedIds.Contains(pickId);

private void ToggleExpand(int pickId)
{
    if (!_expandedIds.Add(pickId))
    {
        _expandedIds.Remove(pickId);
    }
}
```

`_expandedIds.Add` връща `false` ако вече съществува — така toggle е add-or-remove в едно `if`, без отделен `Contains` check.

**Markup**: текстът и иконата на бутона се превключват през същия `IsExpanded` check:

```razor
<MudText Typo="Typo.body2" Class="mt-3">
    @(IsExpanded(pick.Id) ? pick.Rationale : Truncate(pick.Rationale))
</MudText>

<MudIconButton Icon="@(IsExpanded(pick.Id) ? Icons.Material.Filled.ExpandLess : Icons.Material.Filled.ExpandMore)"
               Size="Size.Small"
               OnClick="() => ToggleExpand(pick.Id)" />
```

Truncate helper реже на фиксиран брой символи + `"..."`, null-safe:

```csharp
private static string Truncate(string? rationale)
{
    if (string.IsNullOrEmpty(rationale))
    {
        return string.Empty;
    }

    return rationale.Length <= 120 ? rationale : rationale[..120] + "...";
}
```

За да работи `OnClick` изобщо (interactivity), страницата трябва `@rendermode InteractiveServer` в началото на файла — без него бутоните няма да реагират (компонентите се render-ват статично по подразбиране в Blazor Web App).

## Data loading pattern (за страници, не components, които четат директно от DB)

`@inject IDbContextFactory<AppDbContext> DbContextFactory`, зареждане в `OnInitializedAsync`, кратко-живущ `DbContext` през `await using`:

```razor
@inject IDbContextFactory<AppDbContext> DbContextFactory

@code {
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        await using var dbContext = await DbContextFactory.CreateDbContextAsync();
        // заявки...
        _loading = false;
    }
}
```

Показвай `MudProgressCircular Indeterminate="true" Color="Color.Primary"` докато `_loading`, и `MudAlert Severity="Severity.Info"` когато заявката е успешна, но резултатът е празен — не смесвай двете състояния в едно съобщение.

## Code template за нова dashboard секция

```razor
@page "/xxx"
@rendermode InteractiveServer
@using EarlySignalSystem.Data
@using EarlySignalSystem.Models
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<AppDbContext> DbContextFactory

<PageTitle>Xxx</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="py-6">
    <MudText Typo="Typo.h4" Style="font-weight:700;" Class="mb-6">Xxx</MudText>

    @if (_loading)
    {
        <MudProgressCircular Indeterminate="true" Color="Color.Primary" />
    }
    else if (_items.Count == 0)
    {
        <MudAlert Severity="Severity.Info">Няма данни все още.</MudAlert>
    }
    else
    {
        <MudGrid>
            @foreach (var item in _items)
            {
                <MudItem xs="12" sm="6" md="4">
                    <MudCard Style="background:#1a1d2e; height:100%;">
                        <MudCardContent>
                            <MudStack Row="true" Justify="Justify.SpaceBetween" AlignItems="AlignItems.Start">
                                <MudText Typo="Typo.h6" Style="font-weight:700;">@item.Name</MudText>
                                <MudChip T="string" Color="@GetScoreColor(item.Score)" Variant="Variant.Filled" Size="Size.Small">
                                    @item.Score.ToString("0.0")
                                </MudChip>
                            </MudStack>
                        </MudCardContent>
                    </MudCard>
                </MudItem>
            }
        </MudGrid>
    }
</MudContainer>

@code {
    private bool _loading = true;
    private List<Xxx> _items = [];

    protected override async Task OnInitializedAsync()
    {
        await using var dbContext = await DbContextFactory.CreateDbContextAsync();
        _items = await dbContext.Xxx.ToListAsync();
        _loading = false;
    }

    private static Color GetScoreColor(decimal score) => score switch
    {
        >= 70 => Color.Success,
        >= 40 => Color.Warning,
        _ => Color.Error,
    };
}
```

## Чеклист за нов компонент/страница

- [ ] Никакви hardcoded hex цветове извън `EarlySignalTheme` константите (освен ниво-специфични card фонове като `#20243a`)
- [ ] Score/confidence badge-и → `MudChip T="string"` + `GetScoreColor` helper със същите прагове (70 / 40)
- [ ] Карти в решетка → `MudGrid` + `MudItem xs/sm/md`, не ръчен CSS
- [ ] Spacing през `MudStack Spacing`/`Class="mb-*/mt-*/py-*"`, не inline margin styles
- [ ] Ако има клик/toggle логика → `@rendermode InteractiveServer` в началото на файла
- [ ] DB четене от страница → `IDbContextFactory<AppDbContext>` + `await using` в `OnInitializedAsync`, с `_loading` и empty-state (`MudAlert`) отделно обработени
- [ ] `dotnet build` минава чисто
