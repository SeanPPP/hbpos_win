# POSM sales_order 表结构基准

## 目标

`sales_order` 只保存订单主表核心字段。退款、刷卡流水、分期状态必须从各自分表还原或聚合，不能把汇总字段写回订单主表。

## sales_order 主表字段

当前基准字段：

- `OrderGuid`
- `OrderTime`
- `BranchCode`
- `DeviceCode`
- `TotalAmount`
- `DiscountAmount`
- `ActualAmount`
- `ItemCount`
- `CashierId`
- `CashierName`
- `Status`
- `LastUploadTime`
- `Remark`
- `CreatedBy`
- `CreatedTime`
- `UpdatedBy`
- `UpdatedTime`

以下字段不属于 `sales_order`：

- `CustomerCode`
- `InstallmentPaidAmount`
- `InstallmentRemainingAmount`
- `InstallmentStatus`
- `InstallmentCompletedTime`
- `RefundAmount`
- `RefundItemCount`
- `RefundStatus`
- `LastRefundTime`
- `TradeGuid`
- `OrderSourceType`

## 退款相关表

- `payment_detail`：保存支付明细。退款支付使用负数 `Amount`，`Reference` 保存支付或退款引用。
- `sales_return_record`：保存退货行与原订单、原订单行的关系。
- `BankTransaction`：保存刷卡交易流水。刷卡退款交易使用负数 `Amount`。

退款汇总展示需要按 `OrderGuid` 从上述分表聚合，不从 `sales_order` 读取汇总列。

## 分期相关表

- `InstallmentOrder`：保存分期主记录、客户、已付金额、剩余金额、状态、提货和取消信息。
- `InstallmentOrderLine`：保存分期商品行。
- `InstallmentPayment`：保存分期付款和取消退款记录，含 `IdempotencyKey` 与刷卡交易 JSON。

分期汇总展示需要从 `InstallmentOrder` 与 `InstallmentPayment` 读取，不从 `sales_order` 读取 `Installment*` 字段。

## 本地 SQLite 边界

- `LocalOrders` 不保存分期或退款汇总字段。
- `LocalPayments.IdempotencyKey` 是本地重试和代金券退款恢复辅助字段，不要求同步到后端 `payment_detail`。
- `LocalCardTransactions.Processor`、`LocalCardTransactions.RefundReference` 是本地终端辅助字段，不要求同步到后端 `BankTransaction`。
- `LocalOrderInstallments` 是本地分期快照表，允许用 JSON 保存明细；后端仍以 `InstallmentOrder`、`InstallmentOrderLine`、`InstallmentPayment` 为准。

## 修改原则

如果线上数据库已经有不属于 `sales_order` 的扩展列，代码先停止依赖这些列。物理删列需要单独 DBA 审核，不能和 POS 上传链路修复混在一起执行。
