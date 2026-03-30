"""Apply Task 7.1: Change uploadOperationId from sync_frameID+1 to nextFrame (= predicted_frameID after CommitPredictedFrame)"""
import sys

bm_path = 'D:/unity/hyld-master/hyld-master/Client/Assets/Scripts/Server/Manger/Battle/BattleManger.cs'
with open(bm_path, 'r', encoding='utf-8-sig') as f:
    content = f.read()

errors = []

# --- 7.1: Change uploadOperationId from sync_frameID + 1 to nextFrame ---
old_upload = '            int uploadOperationId = Manger.BattleData.Instance.sync_frameID + 1;'
new_upload = '            // ── Task 7: 上报帧号 = 本帧预测帧号（动态追帧下客户端超前于服务端） ──\n            int uploadOperationId = nextFrame;'

if old_upload not in content:
    errors.append('ERROR 7.1: uploadOperationId anchor not found')
else:
    content = content.replace(old_upload, new_upload, 1)
    print('7.1: uploadOperationId changed from sync_frameID+1 to nextFrame')

if errors:
    for e in errors:
        print(e)
    sys.exit(1)

with open(bm_path, 'w', encoding='utf-8') as f:
    f.write(content)
print('\nTask 7.1 applied successfully')
