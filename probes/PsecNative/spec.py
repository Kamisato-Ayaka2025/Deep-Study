import sys, os, json
sys.path.insert(0, os.path.abspath('.tools/psec-python'))
import flatbuffers
if flatbuffers.__version__ != '25.12.19':
    raise RuntimeError('PSEC probe requires flatbuffers 25.12.19')
b = flatbuffers.Builder(1024)
def strings(items):
    offsets = [b.CreateString(s) for s in items]
    b.StartVector(4, len(offsets), 4)
    for offset in reversed(offsets): b.PrependUOffsetTRelative(offset)
    return b.EndVector()
work = os.path.abspath(sys.argv[1])
rw = strings([work])
read_roots=[os.path.join(os.environ['SystemRoot'], 'System32')]
if len(sys.argv)>4: read_roots += json.load(open(sys.argv[4],encoding='utf-8-sig'))
ro = strings(read_roots)
cap = b.CreateString('registryRead,lpacCom')
deny = strings(sys.argv[3].split(';')) if len(sys.argv)>3 else None
b.StartObject(3)
# No network allow rules, including loopback. The default is deny.
egress = b.EndObject()
b.StartObject(4)
b.PrependUOffsetTRelativeSlot(1, egress, 0)
network = b.EndObject()
b.StartObject(9)
b.PrependUOffsetTRelativeSlot(7, network, 0)
if deny: b.PrependUOffsetTRelativeSlot(6, deny, 0)
b.PrependUOffsetTRelativeSlot(5, ro, 0)
b.PrependUOffsetTRelativeSlot(4, rw, 0)
b.PrependUOffsetTRelativeSlot(1, cap, 0)
b.Prep(2,4)
b.PrependUint16(1)
b.PrependUint16(1)
b.PrependStructSlot(0,b.Offset(),0)
root=b.EndObject()
b.Finish(root, file_identifier=b'PSEC')
open(sys.argv[2], 'wb').write(b.Output())
