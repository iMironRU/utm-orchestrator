package ru.imironru.utm.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public class AgentConfigTest {

    @Test
    public void parsesValidArgs() {
        AgentConfig cfg = AgentConfig.parse("fsrarId=030000123456,logDir=C:\\logs");
        assertEquals("030000123456", cfg.getFsrarId());
        assertEquals("C:\\logs", cfg.resolveLogDir());
    }

    @Test
    public void parsesOnlyFsrarId() {
        AgentConfig cfg = AgentConfig.parse("fsrarId=030000999888");
        assertEquals("030000999888", cfg.getFsrarId());
        // logDir должен резолвиться в APPDATA\utm-update\logs или user.home
        assertTrue(cfg.resolveLogDir().contains("utm-update"));
    }

    @Test
    public void parsesEmptyArgs() {
        AgentConfig cfg = AgentConfig.parse("");
        assertEquals("", cfg.getFsrarId());
    }

    @Test
    public void parsesNullArgs() {
        AgentConfig cfg = AgentConfig.parse(null);
        assertEquals("", cfg.getFsrarId());
    }
}
