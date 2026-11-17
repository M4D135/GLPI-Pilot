import hashlib,json,pathlib,subprocess
folder=pathlib.Path('artifacts/reader-protection');files=json.loads((folder/'files.json').read_text())
def blob(raw):return hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()
path=pathlib.Path('web/js/pages/workplace-tickets.js');raw=path.read_bytes().replace(b'\r\n',b'\n');assert blob(raw)=='8c9f95d2a04c8bbf83e8c085e475b5c7da189928'
text=raw.decode()
old="    // Offline/cached sources and a paused specialist editor must not leave an\n    // empty scanner row between the ticket heading and its action buttons.\n    readIndicator.hidden = simulation || !current?.canRead || ['idle', 'paused', 'unavailable'].includes(readStatus);"
new="    // No scanner for unavailable sources. Keep its geometry while editing so\n    // a focus change cannot move the button between pointer-down and click.\n    readIndicator.hidden = simulation || ctx.state.session?.cachedSource || !current?.canRead || ['idle', 'unavailable'].includes(readStatus);"
assert text.count(old)==1;text=text.replace(old,new)
assert text.count("      waiting: 'Copie disponible")==1
text=text.replace("      waiting: 'Copie disponible","      paused: 'Actualisation en pause',\n      waiting: 'Copie disponible")
old="readError?.message || 'Relance manuelle disponible.' : '';";assert text.count(old)==1
text=text.replace(old,"readError?.message || 'Relance manuelle disponible.' : labels[readStatus] || '';")
raw=text.encode();assert blob(raw)=='085db456d4dbe6c38e963b97b070efb582ac841d';path.write_bytes(raw);(folder/path.name).write_bytes(raw)
for item in files:
 if item['path']==path.as_posix():item['sha']=blob(raw)
path=pathlib.Path('web/css/tickets.css');raw=subprocess.check_output(['git','show','HEAD:'+path.as_posix()]);assert blob(raw)=='e781eeb15a8bb2a8d700bb6e796b720871c6bf4e'
raw+=b'\n/* A live-read status never shifts controls while focus moves during a click. */\n.ticket-live-read-text { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }\n'
assert blob(raw)=='a026e2ffc680d7ba2cfc5f854e02ed410c21c559';path.write_bytes(raw);(folder/path.name).write_bytes(raw)
files.append({'path':path.as_posix(),'sha':blob(raw)});(folder/'files.json').write_text(json.dumps(files,indent=2))
subprocess.run(['git','diff','--check'],check=True)
assert set(subprocess.check_output(['git','diff','--name-only'],text=True).splitlines())==set(f['path'] for f in files)
print(json.dumps(files,indent=2))
