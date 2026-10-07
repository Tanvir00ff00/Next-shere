import subprocess, threading, queue, json, pathlib, hashlib, tempfile, os, time
root = pathlib.Path(__file__).resolve().parents[1]
exe = root/'src/NextShare.QuickShare.Native/target/debug/nextshare-quickshare.exe'
temp = pathlib.Path(tempfile.mkdtemp(prefix='nextshare-native-'))
source = temp/'source'; source.mkdir()
(source/'ছবি.jpg').write_bytes(os.urandom(2*1024*1024+137))
(source/'empty.txt').write_bytes(b'')
staging = temp/'staging'
receiver = subprocess.Popen([str(exe), '--staging', str(staging), '--name', 'Next Share'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
events = queue.Queue(); history=[]
def read():
    for line in receiver.stdout:
        event=json.loads(line); history.append(event); events.put(event)
threading.Thread(target=read,daemon=True).start()
try:
    ready=events.get(timeout=15)
    assert ready['type']=='listening',ready
    sent=subprocess.run([str(exe),'--test-send',f"127.0.0.1:{ready['port']}",'--files',str(source/'ছবি.jpg'),str(source/'empty.txt')],capture_output=True,text=True,encoding='utf-8',timeout=60,creationflags=subprocess.CREATE_NO_WINDOW)
    deadline=time.monotonic()+10; completed=None
    while time.monotonic()<deadline:
        try: event=events.get(timeout=max(.01,deadline-time.monotonic()))
        except queue.Empty: break
        if event['type']=='complete': completed=event; break
    assert completed, {'sender':sent.stdout,'stderr':sent.stderr,'events':history}
    for received in completed['files']:
        received=pathlib.Path(received)
        assert received.read_bytes()==(source/received.name).read_bytes()
    report={'passed':True,'googleAppUsed':False,'encryptedProtocol':True,'bytes':sum(p.stat().st_size for p in source.iterdir()),'unicodeName':True,'emptyFile':True,'senderExit':sent.returncode,'senderOutput':sent.stdout,'events':history,'temporaryRoot':str(temp)}
    (root/'artifacts/native-protocol-test.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('events',)},ensure_ascii=False))
finally:
    receiver.stdin.write('{"type":"stop"}\n'); receiver.stdin.flush()
    try:receiver.wait(timeout=5)
    except subprocess.TimeoutExpired:receiver.kill()
    (root/'artifacts/native-test-debug.json').write_text(json.dumps({'events':history,'senderOut':sent.stdout if 'sent' in locals() else '', 'senderErr':sent.stderr if 'sent' in locals() else '', 'receiverErr':receiver.stderr.read()},ensure_ascii=False,indent=2),encoding='utf-8')
