package ru.imironru.utm.agent;

import net.bytebuddy.agent.builder.AgentBuilder;
import net.bytebuddy.asm.Advice;
import net.bytebuddy.matcher.ElementMatchers;
import net.bytebuddy.utility.JavaModule;

import java.lang.instrument.Instrumentation;

/**
 * Java-агент для cert-driven привязки PKCS#11 слота в UTM ЕГАИС.
 *
 * Использование (в install.bat UTM):
 *   -javaagent:utm-binding-agent.jar=fsrarId=030000123456
 *
 * Перехватывает ru.centerinform.transport.crypto.SunCryptographer.c(String) —
 * Java-метод UTM который выбирает PKCS#11 слот по модели токена.
 * После оригинального выбора проверяет FSRAR-ID сертификата на слоте
 * и при необходимости подменяет на слот с нужным сертификатом.
 */
public class BindingAgent {

    public static void premain(String agentArgs, Instrumentation inst) {
        start(agentArgs, inst);
    }

    public static void agentmain(String agentArgs, Instrumentation inst) {
        start(agentArgs, inst);
    }

    private static void start(String agentArgs, Instrumentation inst) {
        // SunCryptographer грузится обычным app-classloader'ом UTM, а не bootstrap —
        // appendToBootstrapClassLoaderSearch здесь не нужен (и ломает доступ:
        // дублирует классы агента в bootstrap CL, из-за чего package-private
        // вызовы между BindingAgent и остальными классами агента падают
        // с IllegalAccessError, если они резолвятся через разные classloader'ы).
        AgentConfig config = AgentConfig.parse(agentArgs);
        AgentLogger.init(config);

        if (config.getFsrarId() == null || config.getFsrarId().isEmpty()) {
            AgentLogger.warn("fsrarId не задан — агент отключён, UTM работает штатно.");
            return;
        }

        AgentLogger.info("Старт агента: fsrarId=" + config.getFsrarId());
        SlotFilter.setTarget(config.getFsrarId());

        // Инструментируем SunCryptographer.c(String) — Java-метод выбора PKCS#11 слота.
        // (C_GetSlotList — native метод, инструментировать нельзя напрямую)
        new AgentBuilder.Default()
            .with(AgentBuilder.RedefinitionStrategy.RETRANSFORMATION)
            .with(AgentBuilder.TypeStrategy.Default.REDEFINE)
            .with(new AgentBuilder.Listener.Adapter() {
                @Override
                public void onError(String typeName, ClassLoader cl,
                                    JavaModule module, boolean loaded, Throwable t) {
                    AgentLogger.warn("Не удалось инструментировать " + typeName + ": " + t.getMessage());
                }
            })
            .type(ElementMatchers.named("ru.centerinform.transport.crypto.SunCryptographer"))
            .transform((builder, type, cl, module, domain) ->
                builder.visit(
                    Advice.to(SlotSelectorAdvice.class)
                          .on(ElementMatchers.named("c")
                              .and(ElementMatchers.returns(long.class))
                              .and(ElementMatchers.takesArguments(String.class)))
                )
            )
            .installOn(inst);

        AgentLogger.info("Инструментация SunCryptographer.c(String) установлена.");
    }
}
