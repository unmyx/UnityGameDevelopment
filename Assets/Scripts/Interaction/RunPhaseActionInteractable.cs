using UnityEngine;
using Game.Core;
using Game.Core.Events;
using Game.Networking;
using Unity.Netcode;

namespace Game.Interaction
{
    /// <summary>
    /// Minimal phase action trigger used for Step 10 scene loop actions.
    /// </summary>
    public class RunPhaseActionInteractable : BaseInteractable
    {
        private enum InteractionAction
        {
            EndWorkdayAndGoHome,
            SleepStartNextDay,
            SellTrackedStolenLoot,
            PurchaseUpgrade
        }

        [SerializeField]
        private InteractionAction _action = InteractionAction.EndWorkdayAndGoHome;

        [SerializeField]
        private bool _logResult = true;

        [SerializeField]
        [Tooltip("Upgrade ID used when Action is PurchaseUpgrade (for example: inventory_quick_slots, cleaning_tool, welding_tool).")]
        private string _upgradeId = string.Empty;

        public override void Interact()
        {
            if (!CanInteract)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                Debug.LogWarning("[RunPhaseActionInteractable] GameManager instance is unavailable.", this);
                return;
            }

            if (IsNonAuthoritativeNetworkClient()
                && NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority authority))
            {
                switch (_action)
                {
                    case InteractionAction.EndWorkdayAndGoHome:
                        authority.RequestCompleteWorkdayAndGoHome();
                        break;

                    case InteractionAction.SleepStartNextDay:
                        authority.RequestStartNextDay();
                        break;

                    case InteractionAction.SellTrackedStolenLoot:
                        authority.RequestSellTrackedStolenLoot();
                        break;

                    case InteractionAction.PurchaseUpgrade:
                        authority.RequestPurchaseUpgrade(_upgradeId);
                        break;
                }

                return;
            }

            bool success;
            switch (_action)
            {
                case InteractionAction.EndWorkdayAndGoHome:
                    success = gameManager.TryCompleteWorkdayAndRouteToHomeScene();
                    break;

                case InteractionAction.SleepStartNextDay:
                    success = gameManager.TryStartNextDayAndRouteToGameplayScene();
                    PublishFeedback(success
                        ? $"Started day {gameManager.GetCurrentDay()}"
                        : "Could not start next day");
                    break;

                case InteractionAction.SellTrackedStolenLoot:
                    success = gameManager.TrySellTrackedStolenLootInHome(out int soldCount, out int payoutAmount);
                    if (!success)
                    {
                        PublishFeedback("Sell unavailable");
                    }
                    else if (soldCount <= 0 || payoutAmount <= 0)
                    {
                        PublishFeedback("Nothing to sell");
                    }
                    else
                    {
                        PublishFeedback($"Sold items for ${payoutAmount}");
                    }

                    if (_logResult && success)
                    {
                        Game.Core.DevelopmentDiagnostics.Log(
                            $"[RunPhaseActionInteractable] Sell processed. soldCount={soldCount}, payout={payoutAmount}.",
                            this);
                    }
                    return;

                case InteractionAction.PurchaseUpgrade:
                    success = gameManager.TryPurchaseUpgradeInHome(_upgradeId, out int spentCurrency, out int resultingTier);
                    PublishFeedback(success
                        ? $"Upgrade purchased (Tier {resultingTier})"
                        : "Upgrade unavailable");

                    if (_logResult)
                    {
                        Game.Core.DevelopmentDiagnostics.Log(
                            $"[RunPhaseActionInteractable] Purchase processed. upgradeId={_upgradeId}, success={success}, spent={spentCurrency}, resultingTier={resultingTier}.",
                            this);
                    }
                    return;

                default:
                    success = false;
                    break;
            }

            if (_logResult)
            {
                Game.Core.DevelopmentDiagnostics.Log(
                    $"[RunPhaseActionInteractable] Action={_action} success={success}.",
                    this);
            }
        }

        private static void PublishFeedback(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            EventBus.Publish(new PlayerFeedbackEvent(message));
        }

        private static bool IsNonAuthoritativeNetworkClient()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening && manager.IsClient && !manager.IsServer;
        }
    }
}
