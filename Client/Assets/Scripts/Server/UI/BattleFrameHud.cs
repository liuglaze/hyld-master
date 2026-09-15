using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Globalization;
#endif

namespace Manger
{
    public sealed class BattleFrameHud : MonoBehaviour
    {
        private BattleManger battleManger;
        private Text frameText;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const int MaxDelayMs = 2000;
        private InputField dropRateInput;
        private InputField delayMinInput;
        private InputField delayMaxInput;
        private Text statusText;
#endif

        public void Init(BattleManger owner)
        {
            battleManger = owner;
            CreateHud();
            RefreshText();
        }

        private void CreateHud()
        {
            GameObject canvasObject = new GameObject("BattleFrameHudCanvas");
            canvasObject.transform.SetParent(transform, false);

            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject panelObject = new GameObject("FrameHudPanel");
            panelObject.transform.SetParent(canvasObject.transform, false);

            RectTransform panelRect = panelObject.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(16f, -16f);
            panelRect.sizeDelta = new Vector2(360f, 340f);

            Image panelImage = panelObject.AddComponent<Image>();
            panelImage.color = new Color(0f, 0f, 0f, 0.55f);

            GameObject textObject = new GameObject("FrameText");
            textObject.transform.SetParent(panelObject.transform, false);

            RectTransform rectTransform = textObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.anchoredPosition = new Vector2(12f, -10f);
            rectTransform.sizeDelta = new Vector2(336f, 144f);

            frameText = textObject.AddComponent<Text>();
            frameText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            frameText.fontSize = 20;
            frameText.lineSpacing = 1.1f;
            frameText.alignment = TextAnchor.UpperLeft;
            frameText.raycastTarget = false;
            frameText.color = Color.white;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            CreateNetSimPanel(panelObject.transform);
#endif
        }

        private void Update()
        {
            RefreshText();
        }

        private void RefreshText()
        {
            string targetText = battleManger.HasDynamicTarget
                ? battleManger.CurrentTargetFrame.ToString()
                : "等待RTT";
            string rttText = battleManger.IsRttReady
                ? $"{battleManger.SmoothedRttMs:F1}ms / {battleManger.RttVarianceMs:F1}ms"
                : "等待Pong";
            string authorityAgeText = battleManger.CurrentSyncFrame > 0
                ? $"{battleManger.AuthorityAgeMs:F1}ms"
                : "等待权威帧";
            frameText.text =
                $"预测帧: {battleManger.CurrentPredictedFrame}\n" +
                $"目标帧: {targetText}\n" +
                $"同步帧: {battleManger.CurrentSyncFrame}\n" +
                $"RTT/抖动: {rttText}\n" +
                $"权威年龄: {authorityAgeText}\n" +
                $"预测历史: {battleManger.PredictionHistoryCount}/{battleManger.PredictionHistoryWindowSize}";
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void CreateNetSimPanel(Transform parent)
        {
            Text titleText = CreateText(parent, "NetSimTitle", "网络模拟", new Vector2(12f, -164f), new Vector2(336f, 24f), 18);
            titleText.color = new Color(0.65f, 0.9f, 1f, 1f);

            CreateText(parent, "DropRateLabel", "丢包%:", new Vector2(12f, -196f), new Vector2(76f, 28f), 16);
            dropRateInput = CreateInput(parent, "DropRateInput", "30", new Vector2(88f, -194f), new Vector2(70f, 30f));
            dropRateInput.contentType = InputField.ContentType.DecimalNumber;

            CreateText(parent, "DelayMinLabel", "延迟min:", new Vector2(166f, -196f), new Vector2(76f, 28f), 16);
            delayMinInput = CreateInput(parent, "DelayMinInput", "50", new Vector2(242f, -194f), new Vector2(62f, 30f));
            delayMinInput.contentType = InputField.ContentType.IntegerNumber;

            CreateText(parent, "DelayMaxLabel", "延迟max:", new Vector2(12f, -234f), new Vector2(76f, 28f), 16);
            delayMaxInput = CreateInput(parent, "DelayMaxInput", "75", new Vector2(88f, -232f), new Vector2(70f, 30f));
            delayMaxInput.contentType = InputField.ContentType.IntegerNumber;

            CreateButton(parent, "ApplyButton", "应用", new Vector2(166f, -232f), new Vector2(62f, 30f), () => ApplyConfig(false));
            CreateButton(parent, "DisableButton", "关闭模拟", new Vector2(242f, -232f), new Vector2(90f, 30f), () => ApplyConfig(true));

            statusText = CreateText(parent, "NetSimStatus", "当前输入未发送", new Vector2(12f, -276f), new Vector2(336f, 42f), 15);
            statusText.color = new Color(0.92f, 0.92f, 0.92f, 1f);
        }

        private Text CreateText(Transform parent, string objectName, string value, Vector2 anchoredPosition, Vector2 size, int fontSize)
        {
            GameObject textObject = new GameObject(objectName);
            textObject.transform.SetParent(parent, false);

            RectTransform rectTransform = textObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.anchoredPosition = anchoredPosition;
            rectTransform.sizeDelta = size;

            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            text.color = Color.white;
            text.text = value;
            return text;
        }

        private InputField CreateInput(Transform parent, string objectName, string value, Vector2 anchoredPosition, Vector2 size)
        {
            GameObject inputObject = new GameObject(objectName);
            inputObject.transform.SetParent(parent, false);

            RectTransform rectTransform = inputObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.anchoredPosition = anchoredPosition;
            rectTransform.sizeDelta = size;

            Image image = inputObject.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.92f);

            InputField input = inputObject.AddComponent<InputField>();
            input.textComponent = CreateInputText(inputObject.transform, "Text", value, Color.black);
            input.placeholder = CreateInputText(inputObject.transform, "Placeholder", "", new Color(0.35f, 0.35f, 0.35f, 1f));
            input.text = value;
            input.contentType = InputField.ContentType.DecimalNumber;
            return input;
        }

        private Text CreateInputText(Transform parent, string objectName, string value, Color color)
        {
            GameObject textObject = new GameObject(objectName);
            textObject.transform.SetParent(parent, false);

            RectTransform rectTransform = textObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = new Vector2(6f, 2f);
            rectTransform.offsetMax = new Vector2(-4f, -2f);

            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            text.color = color;
            text.text = value;
            return text;
        }

        private void CreateButton(Transform parent, string objectName, string label, Vector2 anchoredPosition, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonObject = new GameObject(objectName);
            buttonObject.transform.SetParent(parent, false);

            RectTransform rectTransform = buttonObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(0f, 1f);
            rectTransform.pivot = new Vector2(0f, 1f);
            rectTransform.anchoredPosition = anchoredPosition;
            rectTransform.sizeDelta = size;

            Image image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.18f, 0.45f, 0.75f, 0.95f);

            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            Text text = CreateText(buttonObject.transform, "Text", label, Vector2.zero, size, 16);
            text.alignment = TextAnchor.MiddleCenter;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
        }

        private void ApplyConfig(bool disable)
        {
            if (disable)
            {
                dropRateInput.text = "0";
                delayMinInput.text = "0";
                delayMaxInput.text = "0";
                SendConfig(0f, 0, 0);
                return;
            }

            if (!TryParseDropRatePercent(dropRateInput.text, out float dropRate)
                || !TryParseDelay(delayMinInput.text, out int delayMinMs)
                || !TryParseDelay(delayMaxInput.text, out int delayMaxMs)
                || delayMinMs > delayMaxMs)
            {
                statusText.text = $"输入无效: 丢包0~100, 延迟0~{MaxDelayMs}, min<=max";
                return;
            }

            SendConfig(dropRate, delayMinMs, delayMaxMs);
        }

        private bool TryParseDropRatePercent(string rawValue, out float dropRate)
        {
            dropRate = 0f;
            if (!float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
            {
                return false;
            }
            if (percent < 0f || percent > 100f)
            {
                return false;
            }
            dropRate = percent / 100f;
            return true;
        }

        private bool TryParseDelay(string rawValue, out int delayMs)
        {
            delayMs = 0;
            if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return false;
            }
            if (parsed < 0 || parsed > MaxDelayMs)
            {
                return false;
            }
            delayMs = parsed;
            return true;
        }

        private void SendConfig(float dropRate, int delayMinMs, int delayMaxMs)
        {
            if (battleManger.SendBattleNetSimConfig(dropRate, delayMinMs, delayMaxMs))
            {
                statusText.text = $"已发送: 丢包{dropRate * 100f:F0}% 延迟{delayMinMs}~{delayMaxMs}ms";
                return;
            }

            statusText.text = "发送失败: 战斗ID未就绪";
        }
#endif
    }
}
