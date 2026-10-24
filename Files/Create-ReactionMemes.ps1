param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$directory = Join-Path $ProjectRoot 'Files\Memes'
[void][IO.Directory]::CreateDirectory($directory)
$cards = @(
    @{id='roger'; text='收到！'; caption='我在，放心说'; tags=@('收到','确认','明白'); color='#DDF5E7'; ink='#17634A'},
    @{id='yay'; text='好耶！'; caption='这波可以'; tags=@('开心','庆祝','赞同'); color='#FFF1C9'; ink='#925116'},
    @{id='thinking'; text='让我想想'; caption='脑袋正在转'; tags=@('思考','等等','疑问'); color='#E0EDFF'; ink='#345A94'}
)
foreach($card in $cards){
    $path=Join-Path $directory ($card.id+'.png')
    if(Test-Path -LiteralPath $path){continue}
    $bitmap=[Drawing.Bitmap]::new(512,384)
    $g=[Drawing.Graphics]::FromImage($bitmap)
    $font=[Drawing.Font]::new('Microsoft YaHei',54,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
    $small=[Drawing.Font]::new('Microsoft YaHei',22,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
    $brush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($card.ink))
    $white=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(170,255,255,255))
    $format=[Drawing.StringFormat]::new()
    try{
        $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint=[Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([Drawing.ColorTranslator]::FromHtml($card.color))
        $g.FillEllipse($white,174,35,164,164)
        $g.FillEllipse($brush,218,91,14,17)
        $g.FillEllipse($brush,278,91,14,17)
        $pen=[Drawing.Pen]::new($brush,5)
        try{$g.DrawArc($pen,229,113,55,31,5,170)}finally{$pen.Dispose()}
        $format.Alignment=[Drawing.StringAlignment]::Center
        $g.DrawString($card.text,$font,$brush,[Drawing.RectangleF]::new(10,214,492,76),$format)
        $g.DrawString($card.caption,$small,$brush,[Drawing.RectangleF]::new(10,304,492,42),$format)
        $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
    }finally{$format.Dispose();$white.Dispose();$brush.Dispose();$small.Dispose();$font.Dispose();$g.Dispose();$bitmap.Dispose()}
}
$manifestPath=Join-Path $directory 'manifest.json'
$manifest=if(Test-Path -LiteralPath $manifestPath){Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json}else{[pscustomobject]@{version=1;items=@()}}
foreach($card in $cards){
    if(@($manifest.items|Where-Object id -eq $card.id).Count -eq 0){
        $manifest.items=@($manifest.items)+[pscustomobject]@{id=$card.id;file=($card.id+'.png');tags=$card.tags}
    }
}
$manifest|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output '已准备3张原创文字反应图；既有图片不覆盖。'
