"""Bind an explicit publication to a successful complete main run and inspected ZIPs."""
import argparse
import datetime
import json
import pathlib
import re

EDITIONS = ('PROD-LIGHT',)


def qualify(run, jobs, commit, run_id, attempt, version):
    if not re.fullmatch(r'[0-9a-f]{40}', commit) or not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Invalid commit or version')
    # The run binds to the exact commit, not to a branch name: a release branch qualified by hand and then pushed
    # fast-forward to main keeps the same commit, so its complete run publishes without a second qualification.
    if (run['id'], run['run_attempt'], run['head_sha'], run['path'], run['status'], run['conclusion']) != (
            run_id, attempt, commit, '.github/workflows/windows.yml', 'completed', 'success'):
        raise ValueError('The selected complete run has not qualified this commit')
    required = {'Windows x64 · contrôles réels et packs', 'PostgreSQL 17 · synchronisation réelle'}
    if not required <= {job['name'] for job in jobs} or any(
            job['status'] != 'completed' or job['conclusion'] != 'success' for job in jobs):
        raise ValueError('Missing, failed, cancelled or skipped qualification job')


def select(run, jobs, artifacts, commit, run_id, attempt, version, digests):
    """Compatibility with historical runs retained as GitHub Actions artifacts."""
    qualify(run, jobs, commit, run_id, attempt, version)
    result = {}
    for edition in EDITIONS:
        digest = digests[edition]
        if not re.fullmatch(r'[0-9a-f]{64}', digest):
            raise ValueError('An inspected SHA-256 is required for the production pack')
        matches = [a for a in artifacts if a['name'] == f'GLPI-Pilot_{version}_{edition}_Windows-x64.zip']
        if len(matches) != 1:
            raise ValueError('Missing or ambiguous original archive')
        artifact = matches[0]
        source = artifact['workflow_run']
        if artifact['expired'] or artifact['digest'] != 'sha256:' + digest or (source['id'], source['head_sha']) != (run_id, commit):
            raise ValueError('Archive does not match the inspected bytes and qualified run')
        result[edition] = artifact['id']
    return result


def built_on_trusted_runner(job):
    # Runner hébergé de GitHub depuis l'arrêt du banc privé, ou runner privé pour les runs historiques.
    labels = set(job['labels'])
    hosted = labels == {'windows-2025'} and job.get('runner_group_name') == 'GitHub Actions'
    return hosted or {'Windows', 'X64', 'self-hosted', 'glpi-pilot'} <= labels


def select_stored(run, jobs, job_log, commit, run_id, attempt, version, digests):
    """Read receipts exclusively from the identified Windows job's official GitHub log."""
    qualify(run, jobs, commit, run_id, attempt, version)
    native = [j for j in jobs if j['name'] == 'Windows x64 · contrôles réels et packs']
    if (len(native) != 1 or (native[0]['run_id'], native[0]['run_attempt'], native[0]['head_sha'])
            != (run_id, attempt, commit) or not built_on_trusted_runner(native[0])):
        raise ValueError('Incorrect Windows build job')
    names = {f'GLPI-Pilot_{version}_{edition}_Windows-x64.zip': edition for edition in EDITIONS}
    steps = {'PROD-LIGHT': 'Conserver le pack PROD-LIGHT Windows (sans runtime WebView2)'}
    windows = {}
    for ed, name in steps.items():
        matches = [step for step in native[0]['steps'] if step['name'] == name]
        if len(matches) != 1 or matches[0]['conclusion'] != 'success':
            raise ValueError('The original was not retained in the Windows build job')
        # GitHub provides step times at second precision and job log lines at subsecond precision.
        start, end = [datetime.datetime.fromisoformat(matches[0][key].replace('Z', '+00:00'))
                      for key in ('started_at', 'completed_at')]
        windows[ed] = (start, end + datetime.timedelta(seconds=1))
    seen = {}
    for line in job_log.splitlines():
        marker = re.fullmatch(r'(\d{4}-\d\d-\d\dT[0-9:.]+Z) GLPI_CI_ORIGINAL (\{.+\})', line)
        if not marker:
            continue
        timestamp = datetime.datetime.fromisoformat(marker[1].replace('Z', '+00:00'))
        receipt = json.loads(marker[2])
        ed = names.get(receipt['name'])
        if ed is None or ed in seen or receipt['bytes'] <= 0 or not re.fullmatch('[0-9a-f]{64}', digests[ed]):
            raise ValueError('Invalid or duplicate original pack receipt')
        if not windows[ed][0] <= timestamp < windows[ed][1]:
            raise ValueError('Receipt outside its Windows retention step')
        if (receipt['repository'], receipt['run'], receipt['attempt'], receipt['commit'], receipt['job'],
                receipt['sha256']) != ('M4D135/GLPI-Pilot', run_id, attempt, commit, 'windows', digests[ed]):
            raise ValueError('Original pack receipt does not match the inspected Windows build')
        seen[ed] = receipt
    if set(seen) != set(EDITIONS):
        raise ValueError('Missing Windows original receipt')
    return seen


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stored', action='store_true', help='Validate original receipts from the native Windows job')
    parser.add_argument('metadata', type=pathlib.Path)
    parser.add_argument('commit')
    parser.add_argument('run_id', type=int)
    parser.add_argument('attempt', type=int)
    parser.add_argument('version')
    parser.add_argument('light_sha256')
    args = parser.parse_args()
    read = lambda name: json.loads((args.metadata / name).read_text())
    jobs = read('jobs.json')
    if len(jobs['jobs']) != jobs['total_count']:
        raise ValueError('Incomplete job metadata')
    digests = {'PROD-LIGHT': args.light_sha256}
    if args.stored:
        chosen = select_stored(read('run.json'), jobs['jobs'], (args.metadata / 'windows-job.log').read_text('utf-8-sig', errors='replace'),
                               args.commit, args.run_id, args.attempt, args.version, digests)
        (args.metadata / 'originals.json').write_text(json.dumps(chosen, indent=2) + '\n')
    else:
        artifacts = read('artifacts.json')
        if len(artifacts['artifacts']) != artifacts['total_count']:
            raise ValueError('Incomplete artifact metadata')
        chosen = select(read('run.json'), jobs['jobs'], artifacts['artifacts'],
                        args.commit, args.run_id, args.attempt, args.version, digests)
        for edition, identity in chosen.items():
            print(f'{edition.lower()}-artifact-id={identity}')
