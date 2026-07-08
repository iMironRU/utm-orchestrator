package ru.imironru.utm.agent;

import net.bytebuddy.asm.Advice;

/**
 * Advice для SunCryptographer.c(String requestedModel) — метода выбора PKCS#11 слота.
 *
 * Вызывается ПОСЛЕ оригинального метода. Получает выбранный слот (long),
 * проверяет FSRAR-ID сертификата на нём, при несовпадении ищет нужный слот.
 * Безопасный fallback: при любой ошибке возвращает оригинальный слот.
 */
public class SlotSelectorAdvice {

    @Advice.OnMethodExit(onThrowable = Throwable.class)
    public static void onExit(
            @Advice.This Object cryptographer,
            @Advice.Argument(0) String requestedModel,
            @Advice.Return(readOnly = false) long slot,
            @Advice.Thrown Throwable thrown
    ) {
        if (thrown != null) return; // оригинальный метод упал — не вмешиваемся
        try {
            long better = SlotFilter.findByFsrarId(cryptographer, slot);
            if (better >= 0 && better != slot) {
                AgentLogger.info("Подмена слота: " + slot + " → " + better);
                slot = better;
            }
        } catch (Throwable t) {
            AgentLogger.error("SlotSelectorAdvice: " + t);
        }
    }
}
