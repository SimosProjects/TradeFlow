import { B } from '../styles/theme.js';
import { Card } from './shared/index.js';

function Toggle({ on, onChange }) {
  return (
    <button
      onClick={onChange}
      style={{
        width: 36,
        height: 20,
        borderRadius: 10,
        border: 'none',
        cursor: 'pointer',
        position: 'relative',
        background: on ? B.am : B.mu2,
        transition: 'background 0.2s',
        padding: 0,
        flexShrink: 0,
      }}
    >
      <div style={{
        position: 'absolute',
        top: 2,
        left: on ? 18 : 2,
        width: 16,
        height: 16,
        borderRadius: 8,
        background: on ? '#fff' : B.mu,
        transition: 'left 0.2s',
      }} />
    </button>
  );
}

function ToggleRow({ label, sublabel, on, onChange }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10 }}>
      <div>
        <div style={{ fontSize: 12, fontWeight: 600, color: on ? B.am : B.tx }}>{label}</div>
        <div style={{ fontSize: 11, color: B.mu, marginTop: 1 }}>{sublabel}</div>
      </div>
      <Toggle on={on} onChange={onChange} />
    </div>
  );
}

// Shown under a row whose block flag is manually pinned — regime auto-sync will never revert
// it, in either direction, until the user changes it again or explicitly returns it to auto.
function PinnedBadge({ text, onReset }) {
  return (
    <div style={{
      marginTop: 6,
      padding: '4px 8px',
      borderRadius: 4,
      background: 'rgba(210,153,34,0.07)',
      border: `1px solid rgba(210,153,34,0.22)`,
      fontSize: 11,
      color: B.am,
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
      gap: 8,
    }}>
      <span>{text}</span>
      <button
        onClick={onReset}
        style={{
          background: 'none',
          border: 'none',
          color: B.am,
          textDecoration: 'underline',
          cursor: 'pointer',
          fontSize: 11,
          padding: 0,
          flexShrink: 0,
          whiteSpace: 'nowrap',
        }}
      >
        Return to auto
      </button>
    </div>
  );
}

/// Session Controls panel — all toggles fully controlled from App.jsx.
/// allowOverrideBlocks: when true, regime checkpoints will not reset block settings.
/// blockXManuallySet: true once the user has explicitly toggled that flag from the dashboard —
/// sticky, regime auto-sync will never revert it until the user changes it again. Pin badges
/// are suppressed while allowOverrideBlocks is on, since the blanket lock notice above already
/// covers all three and a second indicator would be redundant.
export function ControlsPanel({
  allowOverrideBlocks = false,
  blockCalls = false,
  blockCallsManuallySet = false,
  blockHigh  = false,
  blockHighManuallySet = false,
  blockLotto = false,
  blockLottoManuallySet = false,
  paused     = false,
  onToggleAllowOverrideBlocks,
  onTogglePause, onToggleBlockCalls, onToggleBlockHigh, onToggleBlockLotto,
  onResetBlockCallsToAuto, onResetBlockHighToAuto, onResetBlockLottoToAuto,
}) {
  return (
    <Card>
      <div style={{
        fontSize: 10,
        fontWeight: 700,
        letterSpacing: '0.07em',
        textTransform: 'uppercase',
        color: B.mu,
        marginBottom: 12,
      }}>
        Session Controls
      </div>

      <ToggleRow
        label="Override regime"
        sublabel={allowOverrideBlocks
          ? 'Block settings pinned — regime checkpoints ignored'
          : 'Regime controls block settings at each checkpoint'}
        on={allowOverrideBlocks}
        onChange={onToggleAllowOverrideBlocks}
      />
      {allowOverrideBlocks && (
        <div style={{ marginTop: 6, padding: '4px 8px', borderRadius: 4, background: 'rgba(99,190,123,0.07)', border: '1px solid rgba(99,190,123,0.22)', fontSize: 11, color: B.gr }}>
          🔒 Block settings pinned — regime will not reset them
        </div>
      )}

      <div style={{ height: '0.5px', background: B.bd, margin: '10px 0' }} />

      <ToggleRow
        label="Block call entries"
        sublabel={
          blockCalls
            ? (blockCallsManuallySet ? 'Manually pinned — blocked' : 'Active — seeded by Bearish regime')
            : (blockCallsManuallySet ? 'Manually pinned — unblocked' : 'Calls allowed')
        }
        on={blockCalls}
        onChange={onToggleBlockCalls}
      />
      {blockCallsManuallySet && !allowOverrideBlocks ? (
        <PinnedBadge
          text={`📌 Pinned — ${blockCalls ? 'new call entries will be rejected' : 'calls allowed'} until you toggle it again.`}
          onReset={onResetBlockCallsToAuto}
        />
      ) : blockCalls && (
        <div style={{ marginTop: 6, padding: '4px 8px', borderRadius: 4, background: 'rgba(210,153,34,0.07)', border: `1px solid rgba(210,153,34,0.22)`, fontSize: 11, color: B.am }}>
          ⚠ New call entries will be rejected by the risk engine
        </div>
      )}

      <div style={{ height: '0.5px', background: B.bd, margin: '10px 0' }} />

      <ToggleRow
        label="Block high risk"
        sublabel={
          blockHigh
            ? (blockHighManuallySet ? 'Manually pinned — blocked' : 'This-week expiry entries blocked')
            : (blockHighManuallySet ? 'Manually pinned — unblocked' : 'High risk allowed')
        }
        on={blockHigh}
        onChange={onToggleBlockHigh}
      />
      {blockHighManuallySet && !allowOverrideBlocks && (
        <PinnedBadge
          text={`📌 Pinned — ${blockHigh ? 'this-week expiry entries blocked' : 'high risk allowed'} until you toggle it again.`}
          onReset={onResetBlockHighToAuto}
        />
      )}

      <div style={{ height: '0.5px', background: B.bd, margin: '10px 0' }} />

      <ToggleRow
        label="Block lotto (0DTE/1DTE)"
        sublabel={
          blockLotto
            ? (blockLottoManuallySet ? 'Manually pinned — blocked' : '0DTE and 1DTE entries blocked')
            : (blockLottoManuallySet ? 'Manually pinned — unblocked' : 'Lotto allowed')
        }
        on={blockLotto}
        onChange={onToggleBlockLotto}
      />
      {blockLottoManuallySet && !allowOverrideBlocks && (
        <PinnedBadge
          text={`📌 Pinned — ${blockLotto ? '0DTE and 1DTE entries blocked' : 'lotto allowed'} until you toggle it again.`}
          onReset={onResetBlockLottoToAuto}
        />
      )}

      <div style={{ height: '0.5px', background: B.bd, margin: '12px 0' }} />

      <button
        onClick={onTogglePause}
        style={{
          width: '100%',
          padding: '9px 12px',
          borderRadius: 6,
          border: `1px solid ${paused ? B.rd : B.bd}`,
          cursor: 'pointer',
          background: paused ? 'rgba(248,81,73,0.1)' : B.cd2,
          color: paused ? B.rd : B.mu,
          fontWeight: 700,
          fontSize: 12,
          letterSpacing: '0.05em',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          gap: 8,
          transition: 'all 0.15s',
        }}
      >
        <span style={{ width: 7, height: 7, borderRadius: '50%', background: paused ? B.rd : B.gr, flexShrink: 0 }} />
        {paused ? 'RESUME TRADING' : 'PAUSE TRADING'}
      </button>
      {paused && (
        <div style={{ marginTop: 6, fontSize: 11, color: B.mu, textAlign: 'center' }}>
          New entries halted — open positions unaffected
        </div>
      )}
    </Card>
  );
}
