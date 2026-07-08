package ru.imironru.utm.agent;

import net.bytebuddy.agent.builder.AgentBuilder;
import net.bytebuddy.asm.Advice;
import net.bytebuddy.matcher.ElementMatchers;

import java.lang.instrument.Instrumentation;

/**
 * Java-агент для cert-driven привязки PKCS#11 слота в UTM ЕГАИС.
 *
 * Использование (в install.bat UTM):
 *   -javaagent:utm-binding-agent.jar=fsrarId=030000123456
 *
 * При загрузке перехватывает sun.security.pkcs11.wrapper.PKCS11.C_GetSlotList —
 * после оригинального вызова фильтрует список слотов, оставляя только тот,
 * чей токен несёт сертификат с указанным FSRAR-ID.
 */
public class BindingAgent {

    public static void premain(String agentArgs, Instrumentation inst) {
        start(agentArgs, inst);
    }

    public static void agentmain(String agentArgs, Instrumentation inst) {
        start(agentArgs, inst);
    }

    private static void start(String agentArgs, Instrumentation inst) {
        AgentConfig config = AgentConfig.parse(agentArgs);
        AgentLogger.init(config);

        if (config.getFsrarId() == null || config.getFsrarId().isEmpty()) {
            AgentLogger.warn("fsrarId не задан — агент отключён, UTM работает штатно.");
            return;
        }

        AgentLogger.info("Старт агента: fsrarId=" + config.getFsrarId());
        SlotFilter.setTarget(config.getFsrarId());

        // Инструментируем sun.security.pkcs11.wrapper.PKCS11
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
            .type(ElementMatchers.named("sun.security.pkcs11.wrapper.PKCS11"))
            .transform((builder, type, cl, module, domain) ->
                builder.visit(
                    Advice.to(SlotListAdvice.class)
                          .on(ElementMatchers.named("C_GetSlotList"))
                )
            )
            .installOn(inst);

        AgentLogger.info("Инструментация PKCS11.C_GetSlotList установлена.");
    }
}
