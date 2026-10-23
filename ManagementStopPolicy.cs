namespace OperationRecorderApp
{
    public static class ManagementStopPolicy
    {
        public static string Reason(RecState state, bool modalDialog) =>
            state != RecState.Idle ? "記録中または保存中です。記録の保存が完了してから停止してください。" :
            modalDialog ? "ダイアログを閉じてから停止してください。" : null;
    }
}
