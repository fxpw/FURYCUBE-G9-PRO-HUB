"""Real EP0 SET/settings and EP2-busy regression; offline, no device access."""
from emulate_ol4_status import MouseEndpointFixture
from emulate_ol6_carrier import build as old_build

SENSOR=bytes.fromhex('04380100013f0500204080c040e0646403060c121f2e000002ff000000ff000000ffffff0000ffffff00ffff4000ffffff01117700000000')
KEYS=bytes.fromhex('083b010200000300000400001100270d00003c00000500000600000100000100000100000100000100000100000100000100000a000009000000b0')
PACKETS=(SENSOR,bytes.fromhex('050f01')+bytes(12),bytes.fromhex('06090100ff00000000'),
         KEYS,bytes.fromhex('0c0a01fe05fa00000000'))

def out(f,packet):
    f.hid(9,packet[0],len(packet));f.fifo=packet;f.run(0x3778E)

def run_regressions(code,layout,candidate_image=None):
    old_code,old_layout=old_build()
    old=MouseEndpointFixture(old_code,old_layout)
    old.pending_mouse(1);out(old,KEYS);old.poll_input()
    assert old.endpoint_fifo_writes and all(row[3] for row in old.endpoint_fifo_writes)
    checks=0
    for packet in PACKETS:
        for buttons in (0,1,2,3):
            for protocol in (0,1):
                f=MouseEndpointFixture(code,layout,candidate_image)
                f.pending_mouse(buttons);f.set8(0x400B11,protocol)
                before=f.config_snapshot();out(f,packet)
                assert f.get8(0x400B23)==0 and not f.flash_writes
                assert f.config_snapshot()==before # Settings remain queued, not skipped.
                f.poll_input();assert not f.submissions and not f.endpoint_fifo_writes
                f.complete_mouse_transfer();f.poll_input()
                assert len(f.submissions)==1 and f.submissions[0][0]==2
                assert not any(row[3] for row in f.endpoint_fifo_writes)
                checks+=1
    for packet in PACKETS:
        f=MouseEndpointFixture(code,layout,candidate_image)
        f.pending_mouse(1);out(f,packet)
        f.set8(0x400B22,1);f.set8(0x400AF6,1)
        f.uc.mem_write(0x400B42,bytes.fromhex('00270000000000'))
        f.poll_input()
        assert f.submissions==[(1,bytes.fromhex('0000270000000000'))]
        assert f.get8(0x400B23)==0
        checks+=1
    # Normal discovery A0 already has a separate native callback path; it must
    # continue to leave a pending mouse IN transfer alone in both versions.
    for c,l in ((old_code,old_layout),(code,layout)):
        f=MouseEndpointFixture(c,l);f.pending_mouse(1)
        f.submit_query(report=11,length=8,selector=1);f.poll_input()
        assert not f.submissions and not f.endpoint_fifo_writes
        checks+=1
    return dict(settings_input_cases=checks,old_keymap_busy_fifo_reproduced=True,
                pending_mouse_waits_for_real_completion=True,independent_keyboard_preserved=True,
                physical_failure_cause_confirmed=False,device_access=False)

if __name__=='__main__':
    import json
    from emulate_ol7_carrier import build
    print(json.dumps(run_regressions(*build()),indent=2))
