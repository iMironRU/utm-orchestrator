package ru.imironru.utm.agent;

import net.bytebuddy.asm.Advice;

/**
 * Byte Buddy Advice для PKCS11.C_GetSlotList(boolean tokenPresent).
 *
 * Вызывается ПОСЛЕ оригинального метода. Получает массив long[] слотов,
 * делегирует фильтрацию в SlotFilter. Если фильтрация не находит целевой
 * слот — возвращает оригинальный список (безопасный fallback).
 */
public class SlotListAdvice {

    @Advice.OnMethodExit
    public static void onExit(
            @Advice.This Object pkcs11Instance,
            @Advice.Return(readOnly = false) long[] slots
    ) {
        try {
            long[] filtered = SlotFilter.filter(pkcs11Instance, slots);
            if (filtered != null) {
                slots = filtered;
            }
        } catch (Throwable t) {
            // Никогда не роняем UTM — только логируем
            AgentLogger.error("Ошибка в SlotListAdvice: " + t.getMessage());
        }
    }
}
