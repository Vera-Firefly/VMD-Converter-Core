"""CLI/JSON/exit-code smoke test. Standard-library Python only; not a runtime dependency."""
import json
import subprocess
import sys
from pathlib import Path

exe = Path(sys.argv[1]).resolve()
directory = Path(__file__).resolve().parent.parent / 'artifacts' / 'smoke'
directory.mkdir(parents=True, exist_ok=True)


def run(expected, *args):
    result = subprocess.run([str(exe), *map(str, args)], capture_output=True, text=True)
    if result.returncode != expected:
        raise RuntimeError(f'{args}: expected {expected}, got {result.returncode}\n{result.stdout}\n{result.stderr}')
    return result


assert 'HumanPose' in run(0, '--help').stdout
run(1, 'unknown-command')
run(0, 'demo', '--output', directory)
rig = directory / 'rig.json'
poses = directory / 'poses.json'
run(0, 'solve', '--rig', rig, '--input', poses, '--output', directory / 'result.json', '--threads', 2)
run(0, 'decode', '--rig', rig, '--input', directory / 'result.json', '--output', directory / 'decoded.json')
run(0, 'validate', '--rig', rig, '--input', poses, '--output', directory / 'accuracy.json')
data = json.loads(poses.read_text())
data['frames'][0]['reference']['bodyPosition'][0] += 0.1
bad = directory / 'wrong-reference.json'
bad.write_text(json.dumps(data))
run(2, 'validate', '--rig', rig, '--input', bad, '--output', directory / 'failed-accuracy.json')
assert len(json.loads((directory / 'decoded.json').read_text())['frames']) == 60
print('PASS: Native EXE help, error exit 1, demo, solve, decode, strict validation, and accuracy-failure exit 2.')
