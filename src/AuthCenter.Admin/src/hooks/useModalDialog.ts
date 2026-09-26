import { useEffect, useRef, type RefObject } from "react";

/**
 * Opens a native modal dialog while <paramref name="open"/> is true, moves focus into it and gives
 * focus back to the control that opened it when it closes.
 */
export function useModalDialog(open: boolean, initialFocus?: RefObject<HTMLElement | null>) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const node = ref.current;
    if (!open || !node || node.open) return;
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    node.showModal();
    initialFocus?.current?.focus();
    return () => {
      if (node.open) node.close();
      if (previous?.isConnected) previous.focus();
    };
  }, [initialFocus, open]);
  return ref;
}
