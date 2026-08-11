import { PageHeader } from "../../components/PageHeader";

export default function ComingSoonPage({ title, phase }: { title: string; phase: string }) {
  return (
    <>
      <PageHeader eyebrow={phase} title={title} description="Este módulo está definido en el plan de implementación y se habilitará en una entrega vertical posterior." />
      <section className="empty-card"><span aria-hidden="true">◇</span><h2>Preparado para la siguiente entrega</h2><p>La navegación ya tiene una ruta estable; todavía no se realizan llamadas anticipadas a su API.</p></section>
    </>
  );
}
