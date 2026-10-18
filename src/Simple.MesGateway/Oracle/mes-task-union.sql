/*
用途：一次性查询7类MES运输任务。

重要约定：
1. 使用UNION ALL保留原始重复行，由应用按TASK_TYPE + SUBLOT幂等处理。
2. 7个分支位于同一条Oracle SQL中，使用同一语句级一致性快照。
3. 统一输出顺序：TASK_TYPE, SUBLOT, AREA, EQP, STEP, DATES, PACKAGE。
4. 不在SQL中增加上线时间过滤；应用层按2026-08-01 00:00:00（UTC+8）过滤。
5. 本SQL只允许以只读方式执行，严禁对MES执行INSERT、UPDATE、DELETE或DDL。
*/

-- 1. 装片机台 → 焊线/键合派工待送区
SELECT 'DIE_TO_WIRE_STAGING' AS TASK_TYPE,
       t.lot AS SUBLOT,
       tt.area AS AREA,
       t.eqp AS EQP,
       t.step AS STEP,
       t.wgdate AS DATES,
       ttt.PACKAGE AS PACKAGE
FROM   (
        SELECT t.*,
               (
                SELECT MAX(t1.eqp)
                FROM   fw_wip_trans t1
                WHERE  t1.lot = t.lot
                       AND t1.dates = t.wgdate
                       AND t1.task = '完工'
               ) eqp
        FROM   (
                SELECT lot,
                       workorder,
                       step,
                       (
                        SELECT MAX(dates)
                        FROM   fw_wip_trans tt
                        WHERE  tt.lot = t.lot
                               AND tt.task = '完工'
                       ) wgdate
                FROM   v_fw_wip_sublot t
                WHERE  step IN ('焊线', '键合')
                       AND state <> '关闭'
                       AND task = '入库'
               ) t
       ) t,
       fw_eqpres_eqpinformation tt,
       v_fw_pc_workorder ttt
WHERE  t.eqp = tt.eqpno
       AND t.workorder = ttt.workorder
       AND tt.step = '装片'

UNION ALL

-- 2. 装片机台 → 烘箱间
SELECT 'DIE_TO_OVEN' AS TASK_TYPE,
       t.lot AS SUBLOT,
       tt.area AS AREA,
       t.eqp AS EQP,
       t.step AS STEP,
       t.wgdate AS DATES,
       ttt.PACKAGE AS PACKAGE
FROM   (
        SELECT t.*,
               (
                SELECT MAX(t1.eqp)
                FROM   fw_wip_trans t1
                WHERE  t1.lot = t.lot
                       AND t1.dates = t.wgdate
                       AND t1.task = '完工'
               ) eqp
        FROM   (
                SELECT lot,
                       workorder,
                       step,
                       (
                        SELECT MAX(dates)
                        FROM   fw_wip_trans tt
                        WHERE  tt.lot = t.lot
                               AND tt.task = '完工'
                       ) wgdate
                FROM   v_fw_wip_sublot t
                WHERE  step IN ('装片烘烤', '装片压力烘烤')
                       AND state <> '关闭'
                       AND task = '入站'
               ) t
       ) t,
       fw_eqpres_eqpinformation tt,
       v_fw_pc_workorder ttt
WHERE  t.eqp = tt.eqpno
       AND t.workorder = ttt.workorder
       AND tt.step = '装片'

UNION ALL

-- 3. 焊线/键合机台 → 人工质检关卡区
SELECT 'WIRE_TO_GATE' AS TASK_TYPE,
       t.lot AS SUBLOT,
       tt.area AS AREA,
       t.eqp AS EQP,
       t.step AS STEP,
       t.wgdate AS DATES,
       ttt.PACKAGE AS PACKAGE
FROM   (
        SELECT t.*,
               (
                SELECT MAX(t1.eqp)
                FROM   fw_wip_trans t1
                WHERE  t1.lot = t.lot
                       AND t1.dates = t.wgdate
                       AND t1.task = '完工'
               ) eqp
        FROM   (
                SELECT lot,
                       workorder,
                       step,
                       (
                        SELECT MAX(dates)
                        FROM   fw_wip_trans tt
                        WHERE  tt.lot = t.lot
                               AND tt.task = '完工'
                       ) wgdate
                FROM   v_fw_wip_sublot t
                WHERE  step IN ('焊线关卡')
                       AND state <> '关闭'
                       AND task = '入库'
               ) t
       ) t,
       fw_eqpres_eqpinformation tt,
       v_fw_pc_workorder ttt
WHERE  t.eqp = tt.eqpno
       AND t.workorder = ttt.workorder
       AND tt.step IN ('焊线', '键合')

UNION ALL

-- 4. 焊线/键合机台 → 三光区
SELECT 'WIRE_TO_OPTICAL' AS TASK_TYPE,
       t.lot AS SUBLOT,
       tt.area AS AREA,
       t.eqp AS EQP,
       t.step AS STEP,
       t.wgdate AS DATES,
       ttt.PACKAGE AS PACKAGE
FROM   (
        SELECT t.*,
               (
                SELECT MAX(t1.eqp)
                FROM   fw_wip_trans t1
                WHERE  t1.lot = t.lot
                       AND t1.dates = t.wgdate
                       AND t1.task = '完工'
               ) eqp
        FROM   (
                SELECT lot,
                       workorder,
                       step,
                       (
                        SELECT MAX(dates)
                        FROM   fw_wip_trans tt
                        WHERE  tt.lot = t.lot
                               AND tt.task = '完工'
                       ) wgdate
                FROM   v_fw_wip_sublot t
                WHERE  step IN ('三光检验')
                       AND state <> '关闭'
                       AND task = '完工'
               ) t
       ) t,
       fw_eqpres_eqpinformation tt,
       v_fw_pc_workorder ttt
WHERE  t.eqp = tt.eqpno
       AND t.workorder = ttt.workorder
       AND tt.step IN ('焊线', '键合')

UNION ALL

-- 5. 焊线/键合派工待送区 → 指定焊线/键合机台
SELECT 'STAGING_TO_WIRE' AS TASK_TYPE,
       t.lot AS SUBLOT,
       tt.area AS AREA,
       t.eqp AS EQP,
       t.step AS STEP,
       t.lasttime AS DATES,
       ttt.PACKAGE AS PACKAGE
FROM   v_fw_wip_sublot t,
       fw_eqpres_eqpinformation tt,
       v_fw_pc_workorder ttt
WHERE  t.eqp = tt.eqpno
       AND t.workorder = ttt.workorder
       AND t.step IN ('焊线', '键合')
       AND t.task = '入站'
       AND t.state <> '关闭'

UNION ALL

-- 6. 焊线1机台 → 固定氮气柜（WireToNitrogen；不含键合）
SELECT 'WIRE_TO_NITROGEN' AS TASK_TYPE,
       t.lot AS SUBLOT,
       tt.area AS AREA,
       t.eqp AS EQP,
       t.step AS STEP,
       t.wgdate AS DATES,
       ttt.PACKAGE AS PACKAGE
FROM   (
        SELECT t.*,
               (
                SELECT MAX(t1.eqp)
                FROM   fw_wip_trans t1
                WHERE  t1.lot = t.lot
                       AND t1.dates = t.wgdate
                       AND t1.task = '完工'
               ) eqp
        FROM   (
                SELECT lot,
                       workorder,
                       step,
                       (
                        SELECT MAX(dates)
                        FROM   fw_wip_trans tt
                        WHERE  tt.lot = t.lot
                               AND tt.task = '完工'
                       ) wgdate
                FROM   v_fw_wip_sublot t
                WHERE  step = '焊线2'
                       AND state <> '关闭'
                       AND task = '入库'
               ) t
       ) t,
       fw_eqpres_eqpinformation tt,
       v_fw_pc_workorder ttt
WHERE  t.eqp = tt.eqpno
       AND t.workorder = ttt.workorder
       AND tt.step = '焊线'

UNION ALL

-- 7. 焊线/键合机台 → 全检区
SELECT 'WIRE_TO_FULL_INSPECTION' AS TASK_TYPE,
       t.lot AS SUBLOT,
       tt.area AS AREA,
       t.eqp AS EQP,
       t.step AS STEP,
       t.wgdate AS DATES,
       ttt.PACKAGE AS PACKAGE
FROM   (
        SELECT t.*,
               (
                SELECT MAX(t1.eqp)
                FROM   fw_wip_trans t1
                WHERE  t1.lot = t.lot
                       AND t1.dates = t.wgdate
                       AND t1.task = '完工'
               ) eqp
        FROM   (
                SELECT lot,
                       workorder,
                       step,
                       (
                        SELECT MAX(dates)
                        FROM   fw_wip_trans tt
                        WHERE  tt.lot = t.lot
                               AND tt.task = '完工'
                       ) wgdate
                FROM   v_fw_wip_sublot t
                WHERE  step = '焊线全检'
                       AND state <> '关闭'
                       AND task = '完工'
               ) t
       ) t,
       fw_eqpres_eqpinformation tt,
       v_fw_pc_workorder ttt
WHERE  t.eqp = tt.eqpno
       AND t.workorder = ttt.workorder
       AND tt.step IN ('焊线', '键合')
