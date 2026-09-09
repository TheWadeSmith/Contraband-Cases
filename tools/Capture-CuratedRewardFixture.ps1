param(
    [Parameter(Mandatory)][string]$RuntimeRoot,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
# Offline projection of the installed mods' declared clone/override data. This
# does not execute their hooks or pretend to be a capture of a running server.
$native = Get-Content (Join-Path $RuntimeRoot 'SPT_Data/database/templates/items.json') -Raw | ConvertFrom-Json -AsHashtable
$globals = Get-Content (Join-Path $RuntimeRoot 'SPT_Data/database/globals.json') -Raw | ConvertFrom-Json -AsHashtable
$prices = @{}
$handbook = Get-Content (Join-Path $RuntimeRoot 'SPT_Data/database/templates/handbook.json') -Raw | ConvertFrom-Json
foreach ($entry in $handbook.Items) { $prices[$entry.Id] = $entry.Price }
$definitions = @{}
$presets = $globals.ItemPresets
$mods = Join-Path $RuntimeRoot 'user/mods'
foreach ($folder in @('ISB-Aishi/db/CustomItems', 'WTT-ContentBackport/db/CustomItems', 'Natalya/db/CustomItems',
    'Eco-Attachment-Emporium/db/CustomItems', 'Eco-WW2-Pack/db/CustomItems', 'RandomizzatoreMoreCases-4.0/db/CustomItems')) {
    if (!(Test-Path (Join-Path $mods $folder))) { continue }
    foreach ($file in Get-ChildItem (Join-Path $mods $folder) -Filter '*.json' -Recurse) {
        $data = Get-Content $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        foreach ($entry in $data.GetEnumerator()) {
            if ($entry.Value.itemTplToClone) { $definitions[$entry.Key] = $entry.Value }
            foreach ($preset in $entry.Value.weaponPresets) { $presets[$preset._id] = $preset }
        }
    }
}
# Reviewed ordinary-storage declarations from CNN-Containers. Respect disabled
# items and configured grids; trader prices do not replace handbook values.
$cnnPath = Join-Path $mods 'CNN-Containers/config/config.jsonc'
if (Test-Path $cnnPath) {
    $cnn = Get-Content $cnnPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in @(
        @('gearBox', '683d0995deed9b8d4f897ec2', 5, 3, 10, 8, 995000, 'item_container_gearbox', @('57bef4c42459772e8d35a53b','5448e54d4bdc2dcc718b4568','5448e5284bdc2dcb718b4567','5448e53e4bdc2d60728b4567','5645bcb74bdc2ded0b8b4578','5448e5724bdc2ddf718b4568','5a341c4086f77401f2541505','5a341c4686f77469e155819e','5b3f15d486f77432d0509248')),
        @('modCase', '683d09aadb9e219d2f7bd6e8', 3, 2, 6, 5, 197000, 'item_container_modbox', @('5448fe124bdc2da5018b4567')))) {
        $cfg = $cnn[$entry[0]]
        if ($cfg -and $cfg.enabled -eq $false) { continue }
        $id = $entry[1]
        $definitions[$id] = @{ itemTplToClone = '5d235bb686f77443f4331278'; parentId = '5795f317245977243854e041';
            handbookPriceRoubles = $entry[6]; overrideProperties = @{
                Width = $entry[2]; Height = $entry[3];
                Prefab = @{ path = 'assets/content/items/containers/' + $entry[7] + '.bundle'; rcid = '' };
                Grids = @(@{ _id = $id; _name = 'main'; _parent = $id; _proto = '55d329c24bdc2d892f8b4567'; _props = @{
                    cellsH = $(if ($cfg.gridH) { $cfg.gridH } else { $entry[4] });
                    cellsV = $(if ($cfg.gridV) { $cfg.gridV } else { $entry[5] });
                    minCount = 0; maxCount = 0; maxWeight = 0; isSortingTable = $false;
                    filters = @(@{ Filter = @($entry[8]) + @($cfg.extraFilters | Where-Object { $_ });
                        ExcludedFilter = @($cfg.extraExcludedFilters | Where-Object { $_ }) })
                } })
            }
        }
    }
}
$cardNames = @{}
foreach ($folder in @('krackasourus-animeCards', 'krackasourus-pokemonCards', 'krackasourus-yugiohCards')) {
    foreach ($file in Get-ChildItem (Join-Path $mods ($folder + '/config')) -Filter '*.json' -Recurse) {
        $entry = Get-Content $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        if (!$entry.clone_item -or !$entry.id) { continue }
        $props = @{ StackMaxSize = $entry.stack_max_size; Width = $entry.ExternalSize.width; Height = $entry.ExternalSize.height }
        if ($entry.slotStructure) {
            # The installed card port rebuilds legacy non-Mongo slot IDs. These
            # semantic fixture IDs are likewise normalized; exact generated IDs
            # are not claimed, and canonical reward validation does not use them.
            foreach ($slot in $entry.slotStructure) {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($entry.id + '|' + $slot._name)
                $slot._id = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).Substring(0,24).ToLowerInvariant()
                $slot._parent = $entry.id
            }
            $props.Slots = $entry.slotStructure
        }
        $definitions[$entry.id] = @{ itemTplToClone = $entry.clone_item; parentId = $entry.item_parent;
            overrideProperties = $props; handbookPriceRoubles = $entry.price }
        $cardNames[$entry.id] = $entry.item_name
    }
}
foreach ($folder in @('SJX-Stims', 'SJX-Elite-Stims')) {
    foreach ($file in Get-ChildItem (Join-Path $mods ($folder + '/items')) -Filter '*.json' -Recurse) {
        $entry = Get-Content $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        if (!$entry.cloneOrigin -or !$entry.id) { continue }
        $definitions[$entry.id] = @{ itemTplToClone = $entry.cloneOrigin; handbookPriceRoubles = $entry.handBookPrice }
    }
}
# Fixed clone/price declarations inspected from the installed CoolerStims DLL.
# Configurable trader prices are NOT substituted for native handbook values.
foreach ($entry in @(
    @('5c0a1b2c3d4e5f6789abcde1','637b612fb7afa97bfc3d7005',42000),
    @('5c0a1b2c3d4e5f6789abcde3','5c0e534186f7747fa1419867',50000),
    @('5c0a1b2c3d4e5f6789abcde5','5ed51652f6c34d2cc26336a1',55000),
    @('5c0a1b2c3d4e5f6789abcde6','5ed515c8d380ab312177c0fa',42000),
    @('5c0a1b2c3d4e5f6789abcde8','544fb3f34bdc2d03748b456a',22000))) {
    $definitions[$entry[0]] = @{ itemTplToClone=$entry[1]; handbookPriceRoubles=$entry[2] }
}
$natalya = Get-Content (Join-Path $mods 'Natalya/db/CustomWeaponPresets/WeaponPresets.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($entry in $natalya.GetEnumerator()) { $presets[$entry.Key] = $entry.Value }
$ids = Get-Content (Join-Path $mods 'Amonya/db/99_Ids/01_IdDatabase.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($file in Get-ChildItem (Join-Path $mods 'Amonya/db/02_Items') -Filter '*.json') {
    $data = Get-Content $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $data.GetEnumerator()) {
        $id = $ids[$entry.Key + ':ID']
        if ($id) {
            $definitions[$id] = @{ itemTplToClone = $entry.Value.ItemTplToClone; overrideProperties = $entry.Value.Properties; handbookPriceRoubles = $entry.Value.HandbookPriceRoubles }
        }
    }
}
$templates = @{}
$resolving = [System.Collections.Generic.HashSet[string]]::new()
function Resolve-Template([string]$Id) {
    if (!$Id -or $templates.ContainsKey($Id)) { return }
    if (!$resolving.Add($Id)) { throw "Cyclic clone: $Id" }
    if ($definitions.ContainsKey($Id)) {
        $definition = $definitions[$Id]
        Resolve-Template $definition.itemTplToClone
        if (!$templates.ContainsKey($definition.itemTplToClone)) { throw "Missing clone: $Id" }
        $item = $templates[$definition.itemTplToClone] | ConvertTo-Json -Depth 100 | ConvertFrom-Json -AsHashtable
        $item._id = $Id
        if ($definition.parentId) { $item._parent = $definition.parentId }
        if ($definition.overrideProperties) {
            foreach ($property in $definition.overrideProperties.GetEnumerator()) { $item._props[$property.Key] = $property.Value }
        }
        $prices[$Id] = $definition.handbookPriceRoubles
    } elseif ($native.Contains($Id)) { $item = $native[$Id] }
    else { [void]$resolving.Remove($Id); return }
    $templates[$Id] = $item
    Resolve-Template $item._parent
    [void]$resolving.Remove($Id)
}
$selectedPresets = @{}
$packRoot = Join-Path $PSScriptRoot '../config/reward-packs'
foreach ($file in Get-ChildItem $packRoot -Filter '*.json') {
    $pack = Get-Content $file.FullName -Raw | ConvertFrom-Json
    foreach ($id in $pack.requiredTemplateIds) { Resolve-Template $id }
    foreach ($id in $pack.requiredPresetIds) {
        if (!$presets.Contains($id)) { throw "Missing authored preset $id" }
        $selectedPresets[$id] = $presets[$id]
        foreach ($item in $presets[$id]._items) { Resolve-Template $item._tpl }
    }
}
$selectedPrices = @{}
foreach ($id in $templates.Keys) { if ($prices.ContainsKey($id)) { $selectedPrices[$id] = $prices[$id] } }
$fixture = [ordered]@{
    scope = "SPT 4.1.5 plus installed mod clone/override definitions, captured $([DateTime]::UtcNow.ToString('o')); offline, not finalized hook output."
    templates = $templates
    presets = $selectedPresets
    prices = $selectedPrices
    names = $cardNames
    traders = @(Get-ChildItem (Join-Path $RuntimeRoot 'SPT_Data/database/traders') -Directory |
        ForEach-Object { Get-Content (Join-Path $_.FullName 'base.json') -Raw | ConvertFrom-Json -AsHashtable })
}
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputPath), ($fixture | ConvertTo-Json -Depth 100))
Write-Output "Projected $($templates.Count) templates and $($selectedPresets.Count) source presets."
