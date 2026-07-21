# Compare the runtime assembly references baked into a failing plugin vs a known-working one.
[void][System.Reflection.Assembly]::LoadFrom("d:\Claude Projects\askamods\_explore\bin\Debug\net10.0\Mono.Cecil.dll")
foreach ($p in $args) {
    if (-not (Test-Path $p)) { Write-Host "MISSING $p`n"; continue }
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($p)
    Write-Host "### $([System.IO.Path]::GetFileName($p))  (asm ver $($asm.Name.Version))"
    foreach ($r in $asm.MainModule.AssemblyReferences | Sort-Object Name) {
        Write-Host ("    {0,-42} v{1}" -f $r.Name, $r.Version)
    }
    Write-Host ""
}
