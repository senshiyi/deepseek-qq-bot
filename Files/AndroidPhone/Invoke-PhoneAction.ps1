#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateSet('Screenshot', 'Tap', 'Swipe', 'Text', 'Back', 'Home')][string]$Action,
    [int]$X = 0, [int]$Y = 0, [int]$EndX = 0, [int]$EndY = 0,
    [int]$DurationMs = 300, [string]$Text = ''
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AndroidPhone.Core.psm1') -Force
Invoke-AndroidPhoneAction @PSBoundParameters | ConvertTo-Json -Depth 4 -Compress
