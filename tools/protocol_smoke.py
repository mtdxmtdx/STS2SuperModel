import json, pathlib, subprocess, sys
ROOT=pathlib.Path(__file__).resolve().parents[1]
DOTNET='dotnet'
def start(name):
    return subprocess.Popen([DOTNET,str(ROOT/f'src/{name}/bin/Release/net9.0/{name}.dll')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8')
def ask(p,obj):
    p.stdin.write(json.dumps(obj)+'\n');p.stdin.flush()
    line=p.stdout.readline()
    if not line: raise RuntimeError(p.stderr.read())
    return json.loads(line)
trace=[]
worker=start('Nosl.Worker');policy=start('Nosl.PublicPolicy')
try:
    packet=ask(worker,{'op':'reset'})
    initial=packet
    invalid=ask(worker,{'op':'step','action':{'revision':999,'kind':'play','slot':0,'target':0}})
    assert invalid['status']=='invalid_operation'
    assert ask(worker,{'op':'observe'})==initial
    packet=ask(worker,{'op':'sample','samplerSeed':71})
    assert packet==initial, 'sampling exposed a private change before a new public observation'
    for decisions in range(1,201):
        action=ask(policy,packet)
        assert action in packet['actions']
        packet=ask(worker,{'op':'step','action':action})
        trace.append({'action':action,'status':packet['status']})
        if packet['status']=='terminal_settled': break
        assert packet['status'] in ('player_decision','card_choice')
    else: raise AssertionError('diagnostic closure budget exhausted')
    facts=ask(worker,{'op':'settle'})
    assert facts['result'] in ('win','loss')
    assert facts['rewardSelectionsMade']==0
    assert facts==ask(worker,{'op':'settle'})
    trace.append({'terminal':facts})
    (ROOT/'artifacts').mkdir(exist_ok=True)
    (ROOT/'artifacts/protocol-trace.json').write_text(json.dumps(trace,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(f"PROTOCOL PASS reset=real_silent_a10 sample=public_invariant stale=rejected decisions={decisions} result={facts['result']} final_hp={facts['finalHp']} reward_choices=0 policy=separate_process")
finally:
    for p in (worker,policy):
        p.stdin.close(); code=p.wait(timeout=10)
        if code!=0: raise RuntimeError(f'process exit {code}: '+p.stderr.read())
