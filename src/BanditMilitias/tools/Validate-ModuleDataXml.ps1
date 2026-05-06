param(
    [string]$ProjectRoot = (Split-Path -Path $PSScriptRoot -Parent),
    [string]$GameRoot = "",
    [switch]$CheckDistSync
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$issues = New-Object System.Collections.Generic.List[object]

function Add-Issue {
    param(
        [string]$Category,
        [string]$Path,
        [string]$Message
    )

    $issues.Add([pscustomobject]@{
        Category = $Category
        Path = $Path
        Message = $Message
    }) | Out-Null
}

function Get-RelativeProjectPath {
    param([string]$Path)

    if ($Path.StartsWith($ProjectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($ProjectRoot.Length).TrimStart('\', '/')
    }

    return $Path
}

function Resolve-GameRoot {
    param([string]$RequestedRoot)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedRoot)) {
        $candidates += $RequestedRoot
    }

    if (-not [string]::IsNullOrWhiteSpace($env:BANNERLORD_GAME_DIR)) {
        $candidates += $env:BANNERLORD_GAME_DIR
    }

    $candidates += @(
        "C:\Program Files\Epic Games\MountAndBlade2",
        "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
    )

    foreach ($candidate in $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) {
        if (Test-Path (Join-Path $candidate "Modules")) {
            return $candidate
        }
    }

    throw "Bannerlord game root could not be resolved. Pass -GameRoot or set BANNERLORD_GAME_DIR."
}

function Get-SourceXmlFiles {
    Get-ChildItem -Path $ProjectRoot -Recurse -File -Filter *.xml | Where-Object {
        $_.FullName -notmatch '\\dist\\' -and
        $_.FullName -notmatch '\\bin\\' -and
        $_.FullName -notmatch '\\obj\\'
    }
}

function Load-XmlDocument {
    param([string]$Path)

    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null

    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.XmlResolver = $null

    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $doc.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    return $doc
}

function Add-IdsFromText {
    param(
        [hashtable]$Index,
        [string]$Text,
        [string]$Source
    )

    foreach ($match in [regex]::Matches($Text, '\bid="([^"]+)"')) {
        $id = $match.Groups[1].Value
        if (-not $Index.ContainsKey($id)) {
            $Index[$id] = $Source
        }
    }
}

function Build-IdIndexFromFiles {
    param([System.IO.FileInfo[]]$Files)

    $index = @{}
    foreach ($file in $Files) {
        try {
            $content = Get-Content -Raw -Encoding UTF8 -Path $file.FullName
        }
        catch {
            $content = Get-Content -Raw -Path $file.FullName
        }

        Add-IdsFromText -Index $index -Text $content -Source $file.FullName
    }

    return $index
}

function Merge-Index {
    param(
        [hashtable]$Target,
        [hashtable]$Source
    )

    foreach ($key in $Source.Keys) {
        if (-not $Target.ContainsKey($key)) {
            $Target[$key] = $Source[$key]
        }
    }
}

function Test-Reference {
    param(
        [hashtable]$Lookup,
        [string]$FilePath,
        [string]$ReferenceType,
        [string]$Value,
        [string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        Add-Issue -Category "XmlReference" -Path $FilePath -Message "$ReferenceType is empty ($Context)."
        return
    }

    if (-not $Lookup.ContainsKey($Value)) {
        Add-Issue -Category "XmlReference" -Path $FilePath -Message "$ReferenceType '$Value' was not found ($Context)."
    }
}

function Get-StringIdSet {
    param([System.Xml.XmlDocument]$Document)

    $ids = @()
    foreach ($node in @($Document.SelectNodes('//string[@id]'))) {
        $ids += $node.id
    }

    return $ids
}

function Validate-DuplicateIds {
    param(
        [string]$FilePath,
        [string[]]$Ids,
        [string]$Label
    )

    foreach ($group in $Ids | Group-Object | Where-Object { $_.Count -gt 1 }) {
        Add-Issue -Category "DuplicateId" -Path $FilePath -Message "$Label '$($group.Name)' is duplicated ($($group.Count)x)."
    }
}

function Get-OfficialModuleDataFiles {
    param([string]$ResolvedGameRoot)

    $preferredModules = @(
        "Native",
        "SandBoxCore",
        "SandBox",
        "StoryMode",
        "CustomBattle",
        "Multiplayer",
        "BirthAndDeath",
        "Ferries",
        "NavalDLC"
    )

    $moduleRoot = Join-Path $ResolvedGameRoot "Modules"
    $moduleDirs = Get-ChildItem -Path $moduleRoot -Directory | Where-Object { $_.Name -in $preferredModules }
    if ($moduleDirs.Count -eq 0) {
        $moduleDirs = Get-ChildItem -Path $moduleRoot -Directory
    }

    return Get-ChildItem -Path ($moduleDirs | ForEach-Object { $_.FullName }) -Recurse -File -Filter *.xml | Where-Object {
        $_.FullName -match '\\ModuleData\\'
    }
}

function Validate-LocalizationRefs {
    param(
        [hashtable]$Lookup,
        [string]$FilePath,
        [string]$Content
    )

    foreach ($match in [regex]::Matches($Content, '\{=([^}]+)\}')) {
        $locId = $match.Groups[1].Value
        Test-Reference -Lookup $Lookup -FilePath $FilePath -ReferenceType "Localization id" -Value $locId -Context "text reference"
    }
}

$resolvedGameRoot = Resolve-GameRoot -RequestedRoot $GameRoot
$sourceXmlFiles = @(Get-SourceXmlFiles)
if ($sourceXmlFiles.Count -eq 0) {
    throw "No source XML files were found under '$ProjectRoot'."
}

$xmlDocs = @{}
foreach ($file in $sourceXmlFiles) {
    try {
        $xmlDocs[$file.FullName] = Load-XmlDocument -Path $file.FullName
    }
    catch {
        Add-Issue -Category "XmlSyntax" -Path (Get-RelativeProjectPath $file.FullName) -Message $_.Exception.Message
    }
}

$subModulePath = Join-Path $ProjectRoot "SubModule.xml"
$banditsPath = Join-Path $ProjectRoot "ModuleData\bandits.xml"
$lordsPath = Join-Path $ProjectRoot "ModuleData\lords.xml"
$enLocPath = Join-Path $ProjectRoot "ModuleData\Languages\EN\std_BanditMilitias_xml_en.xml"
$trLocPath = Join-Path $ProjectRoot "ModuleData\Languages\TR\std_BanditMilitias_xml_tr.xml"
$prefabPath = Join-Path $ProjectRoot "ModuleData\GUI\Prefabs\LackeyPanel.xml"

$requiredFiles = @($subModulePath, $banditsPath, $lordsPath, $enLocPath, $trLocPath, $prefabPath)
foreach ($path in $requiredFiles) {
    if (-not (Test-Path $path)) {
        Add-Issue -Category "MissingFile" -Path (Get-RelativeProjectPath $path) -Message "Required XML file is missing."
    }
}

if ($xmlDocs.ContainsKey($subModulePath)) {
    foreach ($xmlNameNode in @($xmlDocs[$subModulePath].SelectNodes('/Module/Xmls/XmlNode/XmlName'))) {
        $relativePath = [string]$xmlNameNode.path
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            Add-Issue -Category "SubModule" -Path "SubModule.xml" -Message "XmlName path is empty."
            continue
        }

        $fullPath = Join-Path $ProjectRoot $relativePath
        if (-not (Test-Path $fullPath)) {
            Add-Issue -Category "SubModule" -Path "SubModule.xml" -Message "Referenced path '$relativePath' does not exist."
        }
    }
}

$enIds = @()
$trIds = @()

if ($xmlDocs.ContainsKey($enLocPath)) {
    $enIds = @(Get-StringIdSet -Document $xmlDocs[$enLocPath])
    Validate-DuplicateIds -FilePath (Get-RelativeProjectPath $enLocPath) -Ids $enIds -Label "Localization id"
}

if ($xmlDocs.ContainsKey($trLocPath)) {
    $trIds = @(Get-StringIdSet -Document $xmlDocs[$trLocPath])
    Validate-DuplicateIds -FilePath (Get-RelativeProjectPath $trLocPath) -Ids $trIds -Label "Localization id"
}

foreach ($missingId in $enIds | Where-Object { $_ -notin $trIds }) {
    Add-Issue -Category "LocalizationParity" -Path (Get-RelativeProjectPath $trLocPath) -Message "Missing translation id '$missingId'."
}

foreach ($missingId in $trIds | Where-Object { $_ -notin $enIds }) {
    Add-Issue -Category "LocalizationParity" -Path (Get-RelativeProjectPath $enLocPath) -Message "English localization is missing id '$missingId'."
}

$gameXmlFiles = @(Get-OfficialModuleDataFiles -ResolvedGameRoot $resolvedGameRoot)
$gameIdIndex = Build-IdIndexFromFiles -Files $gameXmlFiles
$modIdIndex = Build-IdIndexFromFiles -Files $sourceXmlFiles
$lookup = @{}
Merge-Index -Target $lookup -Source $gameIdIndex
Merge-Index -Target $lookup -Source $modIdIndex

if ($xmlDocs.ContainsKey($banditsPath)) {
    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//NPCCharacter[@culture]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "Culture" -Value ($node.culture -replace '^Culture\.', '') -Context "NPCCharacter '$($node.id)'"
    }

    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//NPCCharacter[@upgrade_requires]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "ItemCategory" -Value ($node.upgrade_requires -replace '^ItemCategory\.', '') -Context "NPCCharacter '$($node.id)'"
    }

    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//NPCCharacter[@skill_template]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "SkillSet" -Value ($node.skill_template -replace '^SkillSet\.', '') -Context "NPCCharacter '$($node.id)'"
    }

    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//upgrade_target[@id]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "NPCCharacter" -Value ($node.id -replace '^NPCCharacter\.', '') -Context "upgrade_target"
    }

    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//equipment[@id]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "Item" -Value ($node.id -replace '^Item\.', '') -Context "equipment slot '$($node.slot)'"
    }

    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//EquipmentSet[@id]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "EquipmentSet" -Value ([string]$node.id) -Context "equipment template"
    }

    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//face_key_template[@value]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "BodyProperty" -Value ($node.value -replace '^BodyProperty\.', '') -Context "face_key_template"
    }

    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//Trait[@id]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "Trait" -Value ([string]$node.id) -Context "trait reference"
    }

    foreach ($node in @($xmlDocs[$banditsPath].SelectNodes('//skill[@id]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $banditsPath) -ReferenceType "Skill" -Value ([string]$node.id) -Context "skill reference"
    }
}

if ($xmlDocs.ContainsKey($lordsPath)) {
    foreach ($node in @($xmlDocs[$lordsPath].SelectNodes('//NPCCharacter[@culture]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $lordsPath) -ReferenceType "Culture" -Value ($node.culture -replace '^Culture\.', '') -Context "NPCCharacter '$($node.id)'"
    }

    foreach ($node in @($xmlDocs[$lordsPath].SelectNodes('//equipment[@id]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $lordsPath) -ReferenceType "Item" -Value ($node.id -replace '^Item\.', '') -Context "equipment slot '$($node.slot)'"
    }

    foreach ($node in @($xmlDocs[$lordsPath].SelectNodes('//Trait[@id]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $lordsPath) -ReferenceType "Trait" -Value ([string]$node.id) -Context "trait reference"
    }

    foreach ($node in @($xmlDocs[$lordsPath].SelectNodes('//skill[@id]'))) {
        Test-Reference -Lookup $lookup -FilePath (Get-RelativeProjectPath $lordsPath) -ReferenceType "Skill" -Value ([string]$node.id) -Context "skill reference"
    }
}

foreach ($filePath in @($banditsPath, $lordsPath, $prefabPath)) {
    if (-not (Test-Path $filePath)) {
        continue
    }

    try {
        $content = Get-Content -Raw -Encoding UTF8 -Path $filePath
    }
    catch {
        $content = Get-Content -Raw -Path $filePath
    }

    Validate-LocalizationRefs -Lookup $lookup -FilePath (Get-RelativeProjectPath $filePath) -Content $content
}

if ($CheckDistSync) {
    $distRoot = Join-Path $ProjectRoot "dist\BanditMilitias"
    if (Test-Path $distRoot) {
        $compareFiles = @(
            "SubModule.xml",
            "ModuleData\bandits.xml",
            "ModuleData\lords.xml",
            "ModuleData\Languages\EN\std_BanditMilitias_xml_en.xml",
            "ModuleData\Languages\TR\std_BanditMilitias_xml_tr.xml",
            "ModuleData\GUI\Prefabs\LackeyPanel.xml"
        )

        foreach ($relativePath in $compareFiles) {
            $sourcePath = Join-Path $ProjectRoot $relativePath
            $distPath = Join-Path $distRoot $relativePath
            if ((Test-Path $sourcePath) -and (Test-Path $distPath)) {
                $sourceHash = (Get-FileHash $sourcePath).Hash
                $distHash = (Get-FileHash $distPath).Hash
                if ($sourceHash -ne $distHash) {
                    Add-Issue -Category "DistSync" -Path $relativePath -Message "dist copy is out of sync with source XML."
                }
            }
        }
    }
}

Write-Host "=== XML Validator Summary ==="
Write-Host ("ProjectRoot : {0}" -f $ProjectRoot)
Write-Host ("GameRoot    : {0}" -f $resolvedGameRoot)
Write-Host ("Source XMLs : {0}" -f $sourceXmlFiles.Count)
Write-Host ("Game XMLs   : {0}" -f $gameXmlFiles.Count)
Write-Host ("Game IDs    : {0}" -f $gameIdIndex.Count)
Write-Host ("Mod IDs     : {0}" -f $modIdIndex.Count)
Write-Host ("Issues      : {0}" -f $issues.Count)

if ($issues.Count -gt 0) {
    Write-Host ""
    Write-Host "=== XML Validator Issues ==="
    foreach ($issue in $issues | Sort-Object Category, Path, Message) {
        Write-Host ("[{0}] {1}: {2}" -f $issue.Category, $issue.Path, $issue.Message)
    }

    exit 1
}

Write-Host ""
Write-Host "XML validation passed."
exit 0
