from pathlib import Path
import hashlib,json,subprocess
p=Path('.github/workflows/windows.yml')
raw=subprocess.check_output(['git','show','HEAD:'+p.as_posix()])
def blob(b):return hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()
assert blob(raw)=='760c8c9c8b018f2f8258be8c1e22da950715f580'
text=raw.decode()
block='''      - name: Vérifier les moteurs C# et toutes les interfaces WebView
        # Common interface, calendar, sharing, simulation, motion, saved slides and weather/site correction in both editions.
        # Individual suites keep their own bounded deadlines in Invoke-WindowsCi.ps1.
        timeout-minutes: 30
        run: ./build-tools/Invoke-WindowsCi.ps1 -Stage WebViewChecks

'''
assert text.count(block)==1
text=text.replace(block,'')
newjob='''  windows-webview:
    needs: already-qualified
    if: needs.already-qualified.outputs.skip != 'true'
    name: Windows x64 · interfaces WebView complètes
    runs-on: windows-2025
    timeout-minutes: 45
    defaults:
      run:
        shell: pwsh
    env:
      DOTNET_CLI_TELEMETRY_OPTOUT: '1'
      DOTNET_NOLOGO: '1'
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1'
    steps:
      - name: Récupérer les sources
        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1
        with:
          persist-credentials: false
      - name: Installer le SDK .NET 10 qualifié
        id: dotnet
        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68
        with:
          dotnet-version: '10.0.402'
          architecture: x64
      - name: Sélectionner le SDK et préparer les journaux
        env:
          CI_DOTNET_SDK: ${{ steps.dotnet.outputs.dotnet-version }}
        run: |
          if (-not (Test-Path -LiteralPath global.json)) {
            @{ sdk = @{ version = $env:CI_DOTNET_SDK; rollForward = 'latestPatch'; allowPrerelease = $false } } |
              ConvertTo-Json -Depth 3 | Set-Content -LiteralPath global.json -Encoding utf8
          }
          New-Item -ItemType Directory -Path artifacts/windows-ci/logs -Force | Out-Null
          dotnet --info | Tee-Object -FilePath artifacts/windows-ci/logs/dotnet-info.log
          if ($LASTEXITCODE -ne 0) { throw '.NET information unavailable.' }
          if (-not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) { throw 'La qualification des EXE nécessite une session Windows interactive.' }
          "Commit: $env:GITHUB_SHA`nRunner: $env:RUNNER_OS / $env:RUNNER_ARCH`nOS: $([Environment]::OSVersion.VersionString)" |
            Set-Content -LiteralPath artifacts/windows-ci/logs/build-context.log -Encoding utf8
''' + block + '''      - name: Conserver les journaux des interfaces, même en cas d’échec
        if: always()
        uses: ./.github/actions/retain-evidence
        with:
          name: windows-interface-checks-${{ github.run_id }}-${{ github.run_attempt }}
          path: artifacts/windows-ci/logs/

'''
anchor='  windows:\n    needs: already-qualified\n'
assert text.count(anchor)==1
text=text.replace(anchor,newjob+anchor)
text=text.replace('''    # Séparés des packs et lancés en parallèle sur un autre runner hébergé : un échec ou une relance des packs ne
    # refait plus ces 45 min de contrôles déjà verts (gh run rerun --failed ne relance que le job en échec).
    # Plafond : somme des étapes (18+30+10+30 = 88 min) plus la préparation et la conservation des journaux.
''','''    # C# and browser checks run independently of native packs and of the complete WebView stage.
    # Every original suite and per-process deadline is retained; publication requires every job to succeed.
''')
result=text.encode()
assert blob(result)=='08f058f460dfcd2ce93690b8ac62d82224f29fc8','Reviewed workflow differs'
p.write_bytes(result)
folder=Path('artifacts/parallel-qualification');folder.mkdir(parents=True,exist_ok=True)
(folder/'windows.yml').write_bytes(result)
payload=json.dumps({'content':text,'encoding':'utf-8'}).encode()
stored=json.loads(subprocess.check_output(['gh','api','--method','POST','repos/M4D135/GLPI-Pilot/git/blobs','--input','-'],input=payload))
assert stored['sha']==blob(result)
(folder/'verified-blob.json').write_text(json.dumps({'path':p.as_posix(),'sha':blob(result),'base_sha':blob(raw),'stages_removed':0,'assertions_changed':0,'publication_requires_all_jobs':True},indent=2))
print('Stored reviewed qualification workflow: '+blob(result))
