"""Guide refresh regression, using sandbox_test's server and cleanup lifecycle. No playback requests."""
import datetime
import json
import pathlib
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

base = token = name = source = None


def docker(*args):
    return subprocess.check_output(['docker', *args], text=True).strip()


def api(method, path, body=None):
    headers = {'Content-Type': 'application/json', 'Authorization': 'MediaBrowser Token="' + token + '"'}
    request = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None,
                                     headers=headers, method=method)
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read() or b'null')


def wait(predicate, seconds=120):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(1)
    raise AssertionError('Timed out waiting for the guide refresh')


def guide_task():
    return next(task for task in api('GET', '/ScheduledTasks') if task['Key'] == 'RefreshGuide')


def refresh():
    wait(lambda: guide_task()['State'] == 'Idle')
    previous = (guide_task().get('LastExecutionResult') or {}).get('StartTimeUtc')
    api('POST', '/ScheduledTasks/Running/' + guide_task()['Id'])
    result = wait(lambda: (task := guide_task())['State'] == 'Idle'
                  and (task.get('LastExecutionResult') or {}).get('StartTimeUtc') != previous and task)
    assert result['LastExecutionResult']['Status'] == 'Completed', result


def fixtures(folder, count, title):
    now = datetime.datetime.now(datetime.timezone.utc)
    start = (now - datetime.timedelta(minutes=10)).strftime('%Y%m%d%H%M%S +0000')
    end = (now + datetime.timedelta(hours=2)).strftime('%Y%m%d%H%M%S +0000')
    m3u = '#EXTM3U\n'
    xml = '<?xml version="1.0"?><tv>'
    for index in range(1, count + 1):
        m3u += f'#EXTINF:-1 tvg-id="test{index}" tvg-chno="{index}",Test {index}\nhttp://127.0.0.1:1/unused/{index}\n'
        xml += f'<channel id="test{index}"><display-name>Test {index}</display-name></channel>'
        xml += f'<programme channel="test{index}" start="{start}" stop="{end}"><title>{title} {index}</title></programme>'
    for filename, data in (('channels.m3u', m3u), ('guide.xml', xml + '</tv>')):
        path = pathlib.Path(folder, filename)
        path.write_text(data)
        if filename == 'channels.m3u':
            docker('cp', str(path), name + ':/tmp/' + filename)
        elif source:
            docker('cp', str(path), source + ':/data/guide.xml')


# A source on the same Docker network works from a Docker-client container as well as the host.
SERVER = """import http.server, pathlib
root = pathlib.Path('/data')
requests = 0
class Guide(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        global requests
        requests += 1
        (root / 'requests').write_text(str(requests))
        failed = (root / 'fail').exists()
        self.send_response(503 if failed else 200)
        self.send_header('Content-Type', 'application/xml')
        self.send_header('Cache-Control', 'no-cache')
        self.end_headers()
        self.wfile.write(b'unavailable' if failed else (root / 'guide.xml').read_bytes())
    def log_message(self, *args):
        pass
server = http.server.HTTPServer(('0.0.0.0', 8000), Guide)
print('ready', flush=True)
server.serve_forever()
"""


def run(container, address, access_token, labels):
    global name, base, token, source
    name, base, token = container, address, access_token
    with tempfile.TemporaryDirectory() as folder:
        fixtures(folder, 1, 'Original')
        pathlib.Path(folder, 'server.py').write_text(SERVER)
        source = name + '-guide'
        docker('create', '--name', source, *labels, '--network', name + '-net', '--network-alias', 'guide',
               '--memory=128m', '--memory-swap=128m', 'python:3.12-alpine', 'python3', '/data/server.py')
        docker('cp', folder, source + ':/data')
        docker('start', source)
        wait(lambda: 'ready' in docker('logs', source))
        api('POST', '/LiveTv/TunerHosts', {'Type': 'm3u', 'Url': '/tmp/channels.m3u', 'AllowStreamSharing': True})
        api('POST', '/LiveTv/ListingProviders', {'Type': 'xmltv', 'Path': 'http://guide:8000/guide.xml', 'EnableAllTuners': True})
        refresh()
        channels = api('GET', '/LiveTv/Channels')['Items']
        assert len(channels) == 1, channels
        programmes = api('GET', '/LiveTv/Programs?ChannelIds=' + channels[0]['Id'])['Items']
        assert any(p['Name'] == 'Original 1' for p in programmes), programmes
        fixtures(folder, 5, 'Updated')
        refresh()
        channels = api('GET', '/LiveTv/Channels')['Items']
        assert len(channels) == 5, channels
        missing = []
        for channel in channels:
            programmes = api('GET', '/LiveTv/Programs?ChannelIds=' + channel['Id'])['Items']
            expected = 'Updated ' + channel['Number']
            if not any(p['Name'] == expected for p in programmes):
                missing.append({'channel': channel['Name'], 'expected': expected, 'programmes': [p['Name'] for p in programmes]})
        assert not missing, {'fresh_programmes_missing': missing}
        print('PASS: immediate second refresh imported all five fresh programmes, including four newly added channels')
        docker('exec', source, 'touch', '/data/fail')
        previous = docker('exec', source, 'cat', '/data/requests')
        refresh()
        assert docker('exec', source, 'cat', '/data/requests') != previous, 'refresh never attempted the failing HTTP source'
        docker('exec', source, 'rm', '/data/fail')
        fixtures(folder, 5, 'Recovered')
        previous = docker('exec', source, 'cat', '/data/requests')
        refresh()
        assert docker('exec', source, 'cat', '/data/requests') != previous, 'recovered source was skipped by failed-download backoff'
        for channel in api('GET', '/LiveTv/Channels')['Items']:
            programmes = api('GET', '/LiveTv/Programs?ChannelIds=' + channel['Id'])['Items']
            expected = 'Recovered ' + channel['Number']
            assert any(p['Name'] == expected for p in programmes), (expected, programmes)
        print('PASS: failed HTTP download retried immediately after recovery; all five programmes updated')
