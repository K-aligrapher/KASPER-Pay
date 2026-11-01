import { Component, type ReactNode } from "react";
import { Bunny } from "./Bunny";

/** If any screen crashes, show what happened and a reload button instead of a blank window. */
export class ErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <section className="placeholder" style={{ height: "100%" }}>
        <Bunny size={96} />
        <h1>Oops, the screen tripped.</h1>
        <p>Your tasks and approvals are safe; only the display hit an error.</p>
        <pre className="json" style={{ maxWidth: 600 }}>{this.state.error.message}</pre>
        <button className="btn-primary" onClick={() => { location.hash = "#home"; location.reload(); }}>Reload</button>
      </section>
    );
  }
}
