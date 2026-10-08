$ErrorActionPreference = 'Stop'
$tablesDir = 'D:\UnityProject\UnityGame\Config\Excel\Tables'
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false

$wb = $null
$ws = $null
$xlsx = Get-ChildItem -LiteralPath $tablesDir -Filter '*.xlsx' | Where-Object { $_.Name -notlike '~$*' }
foreach ($f in $xlsx) {
	$wbTry = $excel.Workbooks.Open($f.FullName)
	$wsTry = $wbTry.Worksheets.Item(1)
	if (([string]$wsTry.Cells.Item(1, 2).Text) -eq 'Key') {
		$wb = $wbTry
		$ws = $wsTry
		Write-Output ('opened ' + $f.Name)
		break
	}
	$wbTry.Close($false)
}

if ($null -eq $ws) {
	$excel.Quit()
	throw 'language xlsx not found'
}

$ws.Cells.Item(1, 8).Value2 = 'AutoSize'
$ws.Cells.Item(2, 8).Value2 = 'bool'
$ws.Cells.Item(3, 8).Value2 = '阿语一行自动缩放'

$trueKeys = New-Object 'System.Collections.Generic.HashSet[string]'
@(
	'confirm','cancel','back','exit','start','join','room_id','uid','rules',
	'lang_zh','lang_en','lang_ar','again','again_once','you','enter_ludo',
	'ostrich_roll_dice','ostrich_champion','ostrich_last_place',
	'ostrich_champion_bet','ostrich_last_place_bet','ostrich_bet_count',
	'ostrich_segment','ostrich_segment_card','ostrich_segment_bet_card',
	'ostrich_owned','ostrich_available','ostrich_final_card','ostrich_final_card_count',
	'ostrich_put_card','ostrich_round','ostrich_round_1','ostrich_round_2',
	'ostrich_round_3','ostrich_round_4','ostrich_round_5',
	'ostrich_empty_seat','ostrich_robot','ostrich_trustee','ostrich_offline',
	'ostrich_acting','ostrich_acted','ostrich_waiting','ostrich_trustee_on','ostrich_trustee_off',
	'ostrich_color_red','ostrich_color_yellow','ostrich_color_green','ostrich_color_purple',
	'ostrich_color_blue','ostrich_color_pink','ostrich_color_rainbow',
	'ostrich_claim','ostrich_swamp','ostrich_advance','ostrich_desert_card',
	'ostrich_opening','ostrich_settle'
) | ForEach-Object { [void]$trueKeys.Add($_) }

$rows = $ws.UsedRange.Rows.Count
for ($r = 4; $r -le $rows; $r++) {
	$k = [string]$ws.Cells.Item($r, 2).Text
	if ([string]::IsNullOrWhiteSpace($k)) {
		continue
	}
	if ($trueKeys.Contains($k)) {
		$ws.Cells.Item($r, 8).Value2 = $true
	}
	else {
		$ws.Cells.Item($r, 8).Value2 = $false
	}
}

$wb.Save()
$wb.Close($false)
$excel.Quit()
[System.Runtime.Interopservices.Marshal]::ReleaseComObject($ws) | Out-Null
[System.Runtime.Interopservices.Marshal]::ReleaseComObject($wb) | Out-Null
[System.Runtime.Interopservices.Marshal]::ReleaseComObject($excel) | Out-Null
Write-Output 'autosize column written'
