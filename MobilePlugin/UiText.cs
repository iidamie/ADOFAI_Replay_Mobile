namespace Replay.Mobile;

internal sealed record UiText(
    string Recording,
    string ReplayLoading,
    string ReplayWaiting,
    string Replaying,
    string ReplayPaused,
    string ReplayFinished,
    string ReplayFailed,
    string Hits,
    string SaveOptions,
    string SaveFullClear,
    string SaveEveryCompletion,
    string SaveEveryFailure,
    string SaveFailureAt90Percent,
    string DisableAutoReplay,
    string ShowHud,
    string HudSize,
    string MaxFiles,
    string Directory,
    string SaveCurrent,
    string SaveResult,
    string SavingResult,
    string ResultSaved,
    string PlayLast,
    string Pause,
    string Resume,
    string Stop,
    string OpenManager,
    string ManagerTitle,
    string IslandEntry,
    string Search,
    string Files,
    string Refresh,
    string Play,
    string Delete,
    string Complete,
    string Failed,
    string Unsupported,
    string NoFiles,
    string NoAttempt,
    string Queued,
    string Saved,
    string LoadFailed,
    string Close,
    string Previous,
    string Next,
    string Details,
    string RecordedAt,
    string Progress,
    string Inputs,
    string Speed,
    string Source,
    string Official,
    string Custom,
    string ConfirmDelete,
    string Cancel,
    string Page,
    string Artist)
{
    internal static UiText FromLanguage(int language)
    {
        return language switch
        {
            6 or 40 or 41 => Chinese,
            22 => Japanese,
            23 => Korean,
            _ => English,
        };
    }

    private static readonly UiText Chinese = new(
        "录制中", "正在加载回放", "点击游戏画面开始回放", "回放中", "回放已暂停", "回放已结束", "回放在失败处结束",
        "次输入", "自动保存", "从第 1 砖完整通关时保存", "任意起点通关时保存", "每次失败时保存", "失败且进度达到 90% 时保存",
        "不录制自动模式", "显示回放状态", "状态字号", "最多保留回放", "回放目录", "保存当前记录", "保存回放", "正在保存", "回放已保存", "回放最近一局",
        "暂停", "继续", "停止回放", "打开回放管理器", "回放管理器", "回放", "搜索歌曲或作者", "回放文件", "刷新",
        "播放", "删除", "完成", "失败", "不支持", "没有回放文件", "当前没有可保存或回放的记录。", "操作已加入主线程队列。",
        "回放已保存", "无法加载回放", "关闭", "上一页", "下一页", "回放详情", "录制时间", "进度", "输入", "速度", "来源",
        "官方谱面", "自定义谱面", "确认删除此回放？", "取消", "页", "艺术家");

    private static readonly UiText English = new(
        "Recording", "Loading replay", "Tap the game to start replay", "Replaying", "Replay paused", "Replay finished", "Replay ended on failure",
        "inputs", "Auto save", "Save full clears from tile 1", "Save every completion", "Save every failure", "Save failures at 90% or later",
        "Do not record autoplay", "Show replay status", "Status font size", "Maximum replay files", "Replay directory", "Save current attempt", "Save replay", "Saving", "Replay saved", "Replay latest attempt",
        "Pause", "Resume", "Stop replay", "Open replay manager", "Replay Manager", "Replay", "Search song or artist", "Replay files", "Refresh",
        "Play", "Delete", "Complete", "Failed", "Unsupported", "No replay files", "There is no attempt to save or replay.", "Action queued on the game thread.",
        "Replay saved", "Could not load replay", "Close", "Previous", "Next", "Replay details", "Recorded", "Progress", "Inputs", "Speed", "Source",
        "Official level", "Custom level", "Delete this replay?", "Cancel", "Page", "Artist");

    private static readonly UiText Korean = new(
        "녹화 중", "리플레이 불러오는 중", "게임 화면을 눌러 리플레이 시작", "리플레이 중", "리플레이 일시정지", "리플레이 종료", "실패 지점에서 종료",
        "회 입력", "자동 저장", "첫 타일부터 완주 시 저장", "모든 완주 저장", "모든 실패 저장", "90% 이후 실패 저장",
        "오토플레이 녹화 안 함", "리플레이 상태 표시", "상태 글자 크기", "최대 리플레이 수", "리플레이 폴더", "현재 기록 저장", "리플레이 저장", "저장 중", "리플레이 저장 완료", "최근 플레이 보기",
        "일시정지", "계속", "리플레이 중지", "리플레이 관리자 열기", "리플레이 관리자", "리플레이", "곡 또는 아티스트 검색", "리플레이 파일", "새로고침",
        "재생", "삭제", "완료", "실패", "지원 안 함", "리플레이 파일 없음", "저장하거나 재생할 기록이 없습니다.", "게임 메인 스레드에 추가했습니다.",
        "리플레이 저장 완료", "리플레이를 불러올 수 없습니다", "닫기", "이전", "다음", "리플레이 정보", "녹화 시간", "진행률", "입력", "속도", "출처",
        "공식 레벨", "커스텀 레벨", "이 리플레이를 삭제할까요?", "취소", "페이지", "아티스트");

    private static readonly UiText Japanese = new(
        "記録中", "リプレイを読み込み中", "ゲーム画面をタップして開始", "リプレイ中", "一時停止中", "リプレイ終了", "失敗地点で終了",
        "入力", "自動保存", "最初からクリア時に保存", "すべてのクリアを保存", "すべての失敗を保存", "90%以上での失敗を保存",
        "オートプレイを記録しない", "状態を表示", "文字サイズ", "最大保存数", "保存先", "現在の記録を保存", "リプレイを保存", "保存中", "リプレイ保存済み", "直前のプレイを再生",
        "一時停止", "再開", "リプレイ停止", "リプレイ管理を開く", "リプレイ管理", "リプレイ", "曲名またはアーティストを検索", "リプレイファイル", "更新",
        "再生", "削除", "クリア", "失敗", "非対応", "リプレイなし", "保存または再生できる記録がありません。", "ゲームスレッドに追加しました。",
        "リプレイを保存しました", "リプレイを読み込めません", "閉じる", "前へ", "次へ", "リプレイ詳細", "記録日時", "進行度", "入力", "速度", "種類",
        "公式レベル", "カスタムレベル", "このリプレイを削除しますか？", "キャンセル", "ページ", "アーティスト");
}
